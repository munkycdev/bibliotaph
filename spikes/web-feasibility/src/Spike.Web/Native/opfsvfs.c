// A SQLite VFS over the Origin Private File System, for the web spike.
//
// SQLite's file calls are synchronous. In a Web Worker, OPFS offers synchronous access handles, but they can only be
// opened asynchronously, so opfs.js opens one per file name up front (the "pool") and this VFS reads and writes
// through them with EM_JS calls. Files under /opfs/ use the pool; anything else (temp files) goes to the default VFS.
// Without shared memory there is no WAL, so databases here use a rollback journal.
#include <stdlib.h>
#include <string.h>
#include <emscripten.h>

typedef long long sqlite3_int64;
typedef struct sqlite3_io_methods sqlite3_io_methods;
typedef struct sqlite3_file { const sqlite3_io_methods* pMethods; } sqlite3_file;
struct sqlite3_io_methods {
    int iVersion;
    int (*xClose)(sqlite3_file*);
    int (*xRead)(sqlite3_file*, void*, int, sqlite3_int64);
    int (*xWrite)(sqlite3_file*, const void*, int, sqlite3_int64);
    int (*xTruncate)(sqlite3_file*, sqlite3_int64);
    int (*xSync)(sqlite3_file*, int);
    int (*xFileSize)(sqlite3_file*, sqlite3_int64*);
    int (*xLock)(sqlite3_file*, int);
    int (*xUnlock)(sqlite3_file*, int);
    int (*xCheckReservedLock)(sqlite3_file*, int*);
    int (*xFileControl)(sqlite3_file*, int, void*);
    int (*xSectorSize)(sqlite3_file*);
    int (*xDeviceCharacteristics)(sqlite3_file*);
};
typedef struct sqlite3_vfs sqlite3_vfs;
struct sqlite3_vfs {
    int iVersion;
    int szOsFile;
    int mxPathname;
    sqlite3_vfs* pNext;
    const char* zName;
    void* pAppData;
    int (*xOpen)(sqlite3_vfs*, const char*, sqlite3_file*, int, int*);
    int (*xDelete)(sqlite3_vfs*, const char*, int);
    int (*xAccess)(sqlite3_vfs*, const char*, int, int*);
    int (*xFullPathname)(sqlite3_vfs*, const char*, int, char*);
    void* rest[12]; // the Dl*, randomness, sleep, time and syscall entries, copied from the default VFS unchanged
};

extern sqlite3_vfs* sqlite3_vfs_find(const char*);
extern int sqlite3_vfs_register(sqlite3_vfs*, int);

#define SQLITE_OK 0
#define SQLITE_BUSY 5
#define SQLITE_IOERR 10
#define SQLITE_NOTFOUND 12
#define SQLITE_CANTOPEN 14
#define SQLITE_IOERR_SHORT_READ (SQLITE_IOERR | (2 << 8))
#define SQLITE_IOERR_WRITE (SQLITE_IOERR | (3 << 8))
#define LOCK_SHARED 1
#define LOCK_RESERVED 2
#define LOCK_PENDING 3
#define LOCK_EXCLUSIVE 4
#define MAX_FILES 32

EM_JS(int, opfs_lookup, (const char* name), { return globalThis.bibOpfs.lookup(UTF8ToString(name)); });
EM_JS(int, opfs_read, (int id, void* buffer, int amount, double offset), { return globalThis.bibOpfs.read(id, HEAPU8.subarray(buffer, buffer + amount), offset); });
EM_JS(int, opfs_write, (int id, const void* buffer, int amount, double offset), { return globalThis.bibOpfs.write(id, HEAPU8.subarray(buffer, buffer + amount), offset); });
EM_JS(double, opfs_size, (int id), { return globalThis.bibOpfs.size(id); });
EM_JS(int, opfs_truncate, (int id, double size), { return globalThis.bibOpfs.truncate(id, size); });
EM_JS(int, opfs_flush, (int id), { return globalThis.bibOpfs.flush(id); });

typedef struct { sqlite3_file base; int id; int lock; } OpfsFile;

// Locks between connections of this worker, per pooled file: the rules of SQLite's own unix VFS, without the OS.
static struct { int shared; void* reserved; void* pending; void* exclusive; } locks[MAX_FILES];

static sqlite3_vfs vfs;
static sqlite3_vfs* fallback;

// Every call into OPFS crosses from WebAssembly into JavaScript and costs about the same whatever its size, while
// SQLite reads and writes one 4 KB page at a time and its own page cache is per connection and small. So the VFS
// keeps a block cache shared by every connection in the worker: reads are served from it (with a little read-ahead)
// and writes stay in it until SQLite syncs, then go out as a few large writes. It stays correct because this worker
// holds the only access handle to each file. Durability is unchanged: a sync writes the file's dirty blocks before
// flushing, so the journal still reaches disk before the database pages it protects.
#define BLOCK 4096
#define SLOTS 16384
#define READ_AHEAD 8
#define RUN_MAX 256
typedef struct { int id; int len; int dirty; sqlite3_int64 block; } Tag;
static Tag* tags;
static unsigned char* blocks;
static unsigned char* staging;
static int* order;
static double sizes[MAX_FILES];
static char sized[MAX_FILES];

static unsigned Slot(int id, sqlite3_int64 block) { return (unsigned)(((unsigned long long)block * 2654435761u) ^ ((unsigned)id * 40503u)) % SLOTS; }
static unsigned char* Data(unsigned slot) { return blocks + (size_t)slot * BLOCK; }
static int Holds(unsigned slot, int id, sqlite3_int64 block) { return tags[slot].len >= 0 && tags[slot].id == id && tags[slot].block == block; }

static int CacheInit(void)
{
    if (tags) return 1;
    tags = malloc(sizeof(Tag) * SLOTS);
    blocks = malloc((size_t)BLOCK * SLOTS);
    staging = malloc((size_t)BLOCK * RUN_MAX);
    order = malloc(sizeof(int) * SLOTS);
    if (!tags || !blocks || !staging || !order) return 0;
    for (int slot = 0; slot < SLOTS; slot++) { tags[slot].len = -1; tags[slot].dirty = 0; }
    return 1;
}

static double LogicalSize(int id)
{
    if (!sized[id]) { sizes[id] = opfs_size(id); sized[id] = 1; }
    return sizes[id];
}

static int WriteBack(unsigned slot)
{
    Tag* tag = &tags[slot];
    if (!tag->dirty) return SQLITE_OK;
    if (opfs_write(tag->id, Data(slot), tag->len, (double)(tag->block * BLOCK)) != tag->len) return SQLITE_IOERR_WRITE;
    tag->dirty = 0;
    return SQLITE_OK;
}

// Makes the slot for (id, block) hold that block, reading it and the next few uncached blocks in one call.
static int Load(int id, sqlite3_int64 block, unsigned* outSlot)
{
    unsigned slot = Slot(id, block);
    *outSlot = slot;
    if (Holds(slot, id, block)) return SQLITE_OK;
    if (WriteBack(slot) != SQLITE_OK) return SQLITE_IOERR_WRITE;
    double size = LogicalSize(id);
    if ((double)(block * BLOCK) >= size)
    {
        tags[slot] = (Tag){ id, 0, 0, block };
        memset(Data(slot), 0, BLOCK);
        return SQLITE_OK;
    }
    int count = 1;
    while (count < READ_AHEAD && (double)((block + count) * BLOCK) < size)
    {
        unsigned next = Slot(id, block + count);
        if (tags[next].dirty || Holds(next, id, block + count)) break;
        count++;
    }
    int got = opfs_read(id, staging, count * BLOCK, (double)(block * BLOCK));
    if (got < 0) return SQLITE_IOERR;
    for (int k = 0; k < count; k++)
    {
        unsigned target = Slot(id, block + k);
        int len = got - k * BLOCK;
        if (len > BLOCK) len = BLOCK;
        if (len < 0) len = 0;
        if (k > 0 && len == 0) break;
        memcpy(Data(target), staging + (size_t)k * BLOCK, len);
        memset(Data(target) + len, 0, BLOCK - len);
        tags[target] = (Tag){ id, len, 0, block + k };
    }
    return SQLITE_OK;
}

static int BlockOrder(const void* a, const void* b)
{
    sqlite3_int64 x = tags[*(const int*)a].block, y = tags[*(const int*)b].block;
    return x < y ? -1 : x > y;
}

// Writes the file's dirty blocks as runs of contiguous blocks, up to 1 MB per call.
static int Flush(int id)
{
    if (!tags) return SQLITE_OK;
    int n = 0;
    for (int slot = 0; slot < SLOTS; slot++) if (tags[slot].dirty && tags[slot].id == id) order[n++] = slot;
    qsort(order, n, sizeof(int), BlockOrder);
    for (int i = 0; i < n;)
    {
        int j = i;
        while (j + 1 < n && j + 1 - i < RUN_MAX && tags[order[j + 1]].block == tags[order[j]].block + 1) j++;
        int bytes = 0;
        for (int k = i; k <= j; k++)
        {
            memcpy(staging + (size_t)(k - i) * BLOCK, Data(order[k]), BLOCK);
            bytes = (k - i) * BLOCK + tags[order[k]].len;
        }
        if (opfs_write(id, staging, bytes, (double)(tags[order[i]].block * BLOCK)) != bytes) return SQLITE_IOERR_WRITE;
        for (int k = i; k <= j; k++) tags[order[k]].dirty = 0;
        i = j + 1;
    }
    return SQLITE_OK;
}

static int xClose(sqlite3_file* f) { return Flush(((OpfsFile*)f)->id); }

static int xRead(sqlite3_file* f, void* buffer, int amount, sqlite3_int64 offset)
{
    OpfsFile* file = (OpfsFile*)f;
    int result = SQLITE_OK;
    unsigned char* out = buffer;
    while (amount > 0)
    {
        sqlite3_int64 block = offset / BLOCK;
        int within = (int)(offset % BLOCK), take = BLOCK - within < amount ? BLOCK - within : amount;
        unsigned slot;
        int rc = Load(file->id, block, &slot);
        if (rc != SQLITE_OK) return rc;
        int have = tags[slot].len - within;
        if (have < take)
        {
            if (have > 0) memcpy(out, Data(slot) + within, have); else have = 0;
            memset(out + have, 0, take - have);
            result = SQLITE_IOERR_SHORT_READ;
        }
        else memcpy(out, Data(slot) + within, take);
        out += take; offset += take; amount -= take;
    }
    return result;
}

static int xWrite(sqlite3_file* f, const void* buffer, int amount, sqlite3_int64 offset)
{
    OpfsFile* file = (OpfsFile*)f;
    const unsigned char* in = buffer;
    double end = (double)(offset + amount);
    while (amount > 0)
    {
        sqlite3_int64 block = offset / BLOCK;
        int within = (int)(offset % BLOCK), take = BLOCK - within < amount ? BLOCK - within : amount;
        unsigned slot = Slot(file->id, block);
        int rc = SQLITE_OK;
        // A whole-block write needs nothing from disk; a partial one keeps the rest of the block.
        if (take < BLOCK) rc = Load(file->id, block, &slot);
        else if (!Holds(slot, file->id, block) && (rc = WriteBack(slot)) == SQLITE_OK) tags[slot] = (Tag){ file->id, 0, 0, block };
        if (rc != SQLITE_OK) return rc;
        Tag* tag = &tags[slot];
        memcpy(Data(slot) + within, in, take);
        if (tag->len < within + take) tag->len = within + take;
        tag->dirty = 1;
        in += take; offset += take; amount -= take;
    }
    if (end > LogicalSize(file->id)) sizes[file->id] = end;
    return SQLITE_OK;
}

// Drops cached bytes past the new end of the file, dirty or not.
static void Forget(int id, sqlite3_int64 size)
{
    for (int slot = 0; slot < SLOTS; slot++)
    {
        Tag* tag = &tags[slot];
        if (tag->id != id || tag->len < 0) continue;
        if (tag->block * BLOCK >= size) { tag->len = -1; tag->dirty = 0; }
        else if (tag->block * BLOCK + tag->len > size)
        {
            int keep = (int)(size - tag->block * BLOCK);
            memset(Data(slot) + keep, 0, BLOCK - keep);
            tag->len = keep;
        }
    }
}

static int Truncate(int id, sqlite3_int64 size)
{
    Forget(id, size);
    sizes[id] = (double)size;
    sized[id] = 1;
    return opfs_truncate(id, (double)size) == 0 ? SQLITE_OK : SQLITE_IOERR;
}

static int xTruncate(sqlite3_file* f, sqlite3_int64 size) { return Truncate(((OpfsFile*)f)->id, size); }

static int xSync(sqlite3_file* f, int flags)
{
    int rc = Flush(((OpfsFile*)f)->id);
    if (rc != SQLITE_OK) return rc;
    return opfs_flush(((OpfsFile*)f)->id) == 0 ? SQLITE_OK : SQLITE_IOERR;
}

static int xFileSize(sqlite3_file* f, sqlite3_int64* size)
{
    double bytes = LogicalSize(((OpfsFile*)f)->id);
    if (bytes < 0) return SQLITE_IOERR;
    *size = (sqlite3_int64)bytes;
    return SQLITE_OK;
}

static int xLock(sqlite3_file* f, int level)
{
    OpfsFile* file = (OpfsFile*)f;
    if (file->lock >= level) return SQLITE_OK;
    if (level == LOCK_SHARED)
    {
        if (locks[file->id].pending || locks[file->id].exclusive) return SQLITE_BUSY;
        locks[file->id].shared++;
    }
    else if (level == LOCK_RESERVED)
    {
        if (locks[file->id].reserved && locks[file->id].reserved != file) return SQLITE_BUSY;
        locks[file->id].reserved = file;
    }
    else
    {
        if (locks[file->id].pending && locks[file->id].pending != file) return SQLITE_BUSY;
        locks[file->id].pending = file;
        // Other connections still reading: keep PENDING so no new reader starts, and let SQLite retry.
        if (locks[file->id].shared > 1) { file->lock = LOCK_PENDING; return SQLITE_BUSY; }
        locks[file->id].exclusive = file;
    }
    file->lock = level;
    return SQLITE_OK;
}

static int xUnlock(sqlite3_file* f, int level)
{
    OpfsFile* file = (OpfsFile*)f;
    if (file->lock <= level) return SQLITE_OK;
    if (level < LOCK_RESERVED)
    {
        // Without a sync (synchronous = OFF) a finished write would otherwise stay only in memory.
        if (file->lock >= LOCK_RESERVED && Flush(file->id) != SQLITE_OK) return SQLITE_IOERR;
        if (locks[file->id].reserved == file) locks[file->id].reserved = 0;
        if (locks[file->id].pending == file) locks[file->id].pending = 0;
        if (locks[file->id].exclusive == file) locks[file->id].exclusive = 0;
    }
    if (level == 0 && file->lock >= LOCK_SHARED) locks[file->id].shared--;
    file->lock = level;
    return SQLITE_OK;
}

static int xCheckReservedLock(sqlite3_file* f, int* result)
{
    *result = locks[((OpfsFile*)f)->id].reserved != 0;
    return SQLITE_OK;
}

static int xFileControl(sqlite3_file* f, int op, void* arg) { return SQLITE_NOTFOUND; }
static int xSectorSize(sqlite3_file* f) { return 4096; }
static int xDeviceCharacteristics(sqlite3_file* f) { return 0; }

static const sqlite3_io_methods methods = {
    1, xClose, xRead, xWrite, xTruncate, xSync, xFileSize, xLock, xUnlock, xCheckReservedLock, xFileControl, xSectorSize,
    xDeviceCharacteristics,
};

static int IsPooled(const char* name) { return name && strncmp(name, "/opfs/", 6) == 0; }

static int xOpen(sqlite3_vfs* v, const char* name, sqlite3_file* f, int flags, int* outFlags)
{
    if (!IsPooled(name)) return fallback->xOpen(fallback, name, f, flags, outFlags);
    int id = opfs_lookup(name);
    if (id < 0 || id >= MAX_FILES) return SQLITE_CANTOPEN;
    OpfsFile* file = (OpfsFile*)f;
    file->base.pMethods = &methods;
    file->id = id;
    file->lock = 0;
    if (!CacheInit()) return SQLITE_CANTOPEN;
    if (outFlags) *outFlags = flags;
    return SQLITE_OK;
}

// A pooled file can't really be deleted while its handle is open, so deleting empties it; empty counts as absent.
static int xDelete(sqlite3_vfs* v, const char* name, int syncDir)
{
    if (!IsPooled(name)) return fallback->xDelete(fallback, name, syncDir);
    int id = opfs_lookup(name);
    return id < 0 || Truncate(id, 0) == SQLITE_OK ? SQLITE_OK : SQLITE_IOERR;
}

static int xAccess(sqlite3_vfs* v, const char* name, int flags, int* result)
{
    if (!IsPooled(name)) return fallback->xAccess(fallback, name, flags, result);
    int id = opfs_lookup(name);
    *result = id >= 0 && LogicalSize(id) > 0;
    return SQLITE_OK;
}

static int xFullPathname(sqlite3_vfs* v, const char* name, int size, char* out)
{
    if (!IsPooled(name)) return fallback->xFullPathname(fallback, name, size, out);
    strncpy(out, name, size);
    out[size - 1] = 0;
    return SQLITE_OK;
}

// Writes every dirty block and forgets the cache, before the pool's handles close and its ids are reused.
int bib_opfs_reset(void)
{
    int rc = SQLITE_OK;
    for (int id = 0; id < MAX_FILES; id++) { if (Flush(id) != SQLITE_OK) rc = SQLITE_IOERR_WRITE; sized[id] = 0; }
    if (tags) for (int slot = 0; slot < SLOTS; slot++) { tags[slot].len = -1; tags[slot].dirty = 0; }
    return rc;
}

// Makes this VFS the default, so connection strings need no vfs= parameter. Safe to call more than once.
int bib_opfs_register(void)
{
    if (fallback) return SQLITE_OK;
    fallback = sqlite3_vfs_find(0);
    if (!fallback) return SQLITE_IOERR;
    memcpy(&vfs, fallback, sizeof vfs);
    vfs.zName = "opfs";
    vfs.pNext = 0;
    if (vfs.szOsFile < (int)sizeof(OpfsFile)) vfs.szOsFile = sizeof(OpfsFile);
    vfs.xOpen = xOpen;
    vfs.xDelete = xDelete;
    vfs.xAccess = xAccess;
    vfs.xFullPathname = xFullPathname;
    return sqlite3_vfs_register(&vfs, 1);
}
