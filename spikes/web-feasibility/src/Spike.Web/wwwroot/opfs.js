// The worker's side of Native/opfsvfs.c: a pool of OPFS synchronous access handles, opened before SQLite needs them.
const ids = new Map();
const handles = [];

globalThis.bibOpfs = {
  lookup: name => ids.get(name) ?? -1,
  read(id, view, offset) {
    try { return handles[id].read(view, { at: offset }); } catch (e) { console.error('opfs read', e); return -1; }
  },
  write(id, view, offset) {
    try { return handles[id].write(view, { at: offset }); } catch (e) { console.error('opfs write', e); return -1; }
  },
  size(id) {
    try { return handles[id].getSize(); } catch (e) { console.error('opfs size', e); return -1; }
  },
  truncate(id, size) {
    try { handles[id].truncate(size); return 0; } catch (e) { console.error('opfs truncate', e); return -1; }
  },
  flush(id) {
    try { handles[id].flush(); return 0; } catch (e) { console.error('opfs flush', e); return -1; }
  },
};

/** Opens a handle for each '|'-separated file name in the OPFS folder; SQLite sees them as /opfs/<name>. */
export async function openPool(folder, names) {
  const root = await navigator.storage.getDirectory();
  const dir = await root.getDirectoryHandle(folder, { create: true });
  for (const name of names.split('|')) {
    if (ids.has(`/opfs/${name}`)) continue;
    const file = await dir.getFileHandle(name, { create: true });
    handles.push(await file.createSyncAccessHandle());
    ids.set(`/opfs/${name}`, handles.length - 1);
  }
}

/** Closes every handle, which releases the files to other tabs, and empties the pool. */
export function closePool() {
  for (const handle of handles) handle.close();
  handles.length = 0;
  ids.clear();
}

/** Deletes the folder, for a clean run. */
export async function removeFolder(folder) {
  const root = await navigator.storage.getDirectory();
  try { await root.removeEntry(folder, { recursive: true }); } catch { /* absent */ }
}

/** Total bytes stored in the folder's files. */
export async function folderBytes(folder) {
  const root = await navigator.storage.getDirectory();
  const dir = await root.getDirectoryHandle(folder, { create: true });
  let total = 0;
  for await (const handle of dir.values()) if (handle.kind === 'file') total += (await handle.getFile()).size;
  return total;
}
