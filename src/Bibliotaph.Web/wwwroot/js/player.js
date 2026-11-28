// The player's sound: two <audio> elements, the music and an ambience layer that loops under it, each fed a track's
// whole file as a blob. A track's bytes come through the same cache as a book's, so one played before plays again
// without the network. The music shows in the browser's and the system's media controls (the Media Session API),
// whose keys come back to the page as commands; the ambience stays out of them.
import { BookCache, trim } from './bookcache.js';

let owner = null;
const layers = {};

class Layer {
  constructor(name, loop) {
    this.name = name;
    this.audio = new Audio();
    this.audio.preload = 'auto';
    this.audio.loop = loop;
    this.url = null;
    this.ready = false;
    this.load = 0;
    this.lastTick = 0;
    this.audio.addEventListener('ended', () => { if (!this.audio.loop) owner?.invokeMethodAsync('Ended', this.name); });
    this.audio.addEventListener('error', () => {
      // Only once it played: a file that won't open says why to open().
      if (this.ready) owner?.invokeMethodAsync('Failed', this.name, failure(this.audio.error));
    });
    this.audio.addEventListener('timeupdate', () => {
      // A few times a second is enough for the bar; timeupdate can come much faster.
      const now = performance.now();
      if (this.name !== 'music' || now - this.lastTick < 240) return;
      this.lastTick = now;
      owner?.invokeMethodAsync('Tick', this.audio.currentTime, finite(this.audio.duration));
      position(this.audio);
    });
  }

  async open(track) {
    const load = ++this.load;
    this.close();
    this.load = load;
    let bytes;
    try {
      const cache = await BookCache.open(track.key, track.size, () => owner.invokeMethodAsync('Link', track.documentId),
        { title: track.title, entryId: track.entryId, documentId: track.documentId });
      trim(track.key).catch(() => { });
      bytes = await cache.read(0, track.size);
    } catch (e) {
      return load === this.load ? "The file couldn't be read from your storage. Its drive may need connecting again." : 'moved on';
    }
    if (load !== this.load) return 'moved on';
    this.url = URL.createObjectURL(new Blob([bytes], { type: track.mediaType }));
    this.audio.src = this.url;
    try {
      await new Promise((resolve, reject) => {
        this.audio.addEventListener('canplay', resolve, { once: true });
        this.audio.addEventListener('error', () => reject(this.audio.error), { once: true });
      });
    } catch (e) {
      if (load !== this.load) return 'moved on';
      this.close();
      return failure(e);
    }
    if (load !== this.load) return 'moved on';
    this.ready = true;
    return null;
  }

  close() {
    this.load++;
    this.ready = false;
    this.audio.pause();
    if (this.url) {
      this.audio.removeAttribute('src');
      this.audio.load();
      URL.revokeObjectURL(this.url);
      this.url = null;
    }
  }
}

function finite(value) { return Number.isFinite(value) ? value : 0; }

function failure(error) {
  switch (error?.code) {
    case 4: return "This browser can't play this kind of audio file.";
    case 3: return 'The file is damaged, so the browser stopped playing it.';
    default: return 'The browser stopped playing the file.';
  }
}

function position(audio) {
  try {
    if (navigator.mediaSession?.setPositionState && Number.isFinite(audio.duration) && audio.duration > 0)
      navigator.mediaSession.setPositionState({ duration: audio.duration, position: Math.min(audio.currentTime, audio.duration), playbackRate: 1 });
  } catch { }
}

/** Starts the player for the page, which hears back through Ended, Failed, Tick, Key and Link. */
export function init(page) {
  owner = page;
  layers.music ??= new Layer('music', false);
  layers.ambience ??= new Layer('ambience', true);
  const session = navigator.mediaSession;
  if (!session) return;
  for (const [action, key] of [['play', 'play'], ['pause', 'pause'], ['nexttrack', 'next'], ['previoustrack', 'previous'], ['stop', 'stop']]) {
    try { session.setActionHandler(action, () => owner?.invokeMethodAsync('Key', key)); } catch { }
  }
  try { session.setActionHandler('seekto', e => { layers.music.audio.currentTime = e.seekTime; position(layers.music.audio); }); } catch { }
}

/**
 * Loads a track on a layer, ready to play: null once it can, else why it can't.
 * @param {'music'|'ambience'} layer
 * @param {{key: string, size: number, mediaType: string, title: string, entryId: number, documentId: number}} track
 */
export function open(layer, track) { return layers[layer].open(track); }

export async function play(layer) {
  try {
    await layers[layer].audio.play();
    return true;
  } catch {
    // The browser wants a click first: the bar shows Play.
    return false;
  }
}

export function pause(layer) { layers[layer].audio.pause(); }

export function close(layer) { layers[layer].close(); }

export function seek(seconds) {
  const audio = layers.music.audio;
  audio.currentTime = Math.max(0, seconds);
  position(audio);
}

export function restart() { seek(0); }

export function where() { return layers.music.audio.currentTime; }

export function volume(layer, value) { layers[layer].audio.volume = Math.min(1, Math.max(0, value)); }

/** What the media controls show for the music, or nothing. */
export function nowPlaying(track, playing) {
  const session = navigator.mediaSession;
  if (!session) return;
  if (!track) {
    session.metadata = null;
    session.playbackState = 'none';
    return;
  }
  session.metadata = new MediaMetadata({
    title: track.title,
    artist: track.artist ?? '',
    album: track.album ?? '',
    artwork: track.cover ? [{ src: track.cover }] : [],
  });
  session.playbackState = playing ? 'playing' : 'paused';
  try {
    session.setActionHandler('nexttrack', track.canGoNext ? () => owner?.invokeMethodAsync('Key', 'next') : null);
  } catch { }
}
