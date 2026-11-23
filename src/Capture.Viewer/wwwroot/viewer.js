// Capture Viewer
//
// A test client for the capture client. It connects to the capture client's WebSocket, shows what
// the capture client is doing, plays its video streams (one per screen, or one for the whole
// desktop) and lets you control it. Each screen can be zoomed with the mouse wheel and moved into
// a window of its own.
//
// Text messages on the socket are JSON:
//   capture -> viewer   {"type":"state", layout, sources, recording, streaming, video, audio, remoteControl}
//                       {"type":"error","message":...}
//                       {"type":"offer","sdp":...,"sources":{"<mid>":"<stream id>",...}}  and ICE candidates
//   viewer -> capture   {"type":"select","sources":["screen1",...],"audio":true}    what this window shows
//                       {"type":"command","target":"recording"|"streaming","action":"start"|"stop"|"pause"|"resume"}
//                       {"type":"configure","frameRate":15}  /  "videoMode":"h264"|"jpeg"  /
//                         "audioDevices":["default-input","<device id>",...]  /  "layout":"separate"|"desktop"
//                       {"type":"answer","sdp":...}  and ICE candidates
//
// The state's audio.devices lists every audio device the capture client can record, recording
// devices ("input") and playback devices ("output", recorded as what they play), each with its
// id, whether it is selected and how it is doing. "default-input" and "default-output" stand for
// Windows' default devices and follow them when they change.
//
// Media arrives in one of two ways, chosen by the capture client's video mode:
//   h264  a WebRTC session offered by the capture client: one H.264 track per stream, G.711 audio
//   jpeg  binary messages on the socket: [type: 1 byte][stream number: 1 byte][timestamp: int64 LE, 100 ns][payload],
//         type 1 = a JPEG picture of that stream, type 2 = 16-bit little-endian PCM at 8 kHz mono
//
// A popped-out screen is this same page in a window of its own (?popout=<stream id>), with its
// own connection that asks for that one stream and no audio.

const DEFAULT_URL = 'ws://localhost:8081/';
const URL_STORAGE_KEY = 'capture-viewer.url';
const STATS_INTERVAL_MS = 1000;
const MAX_LOG_LINES = 300;
const MAX_ZOOM = 8;
const EMPTY = '–';

const PICTURE_MESSAGE = 1;
const AUDIO_MESSAGE = 2;
const HEADER_LENGTH = 10;
const PCM_SAMPLE_RATE = 8000;

const STAT_FIELDS = {
  video: ['Streams', 'Codec', 'Frame rate', 'Bit rate', 'Frames shown', 'Frames dropped', 'Key frames', 'Packets lost', 'Jitter'],
  audio: ['Codec', 'Bit rate', 'Packets received', 'Packets lost', 'Buffered'],
  connection: ['Transport', 'Route', 'Round trip', 'Received', 'Connected for'],
};

const parameters = new URLSearchParams(location.search);

/** In a pop-out window: the id of the one stream it shows. */
const POPOUT = parameters.get('popout');

const byId = (id) => document.getElementById(id);

const el = {
  form: byId('connect-form'),
  url: byId('url'),
  connect: byId('connect'),
  status: byId('status'),
  stage: byId('stage'),
  tiles: byId('tiles'),
  placeholder: byId('placeholder'),
  placeholderTitle: byId('placeholder-title'),
  placeholderText: byId('placeholder-text'),
  pausedBadge: byId('paused-badge'),
  unmute: byId('unmute'),
  mute: byId('mute'),
  fullscreen: byId('fullscreen'),
  audio: byId('audio'),
  level: byId('level'),
  log: byId('log'),
  clearLog: byId('clear-log'),
  controlNote: byId('control-note'),
  layout: byId('layout'),
  videoMode: byId('video-mode'),
  frameRate: byId('frame-rate'),
  audioDevices: byId('audio-devices'),
  audioNote: byId('audio-note'),
  audioGroups: byId('audio-groups'),
  audioInputs: byId('audio-inputs'),
  audioOutputs: byId('audio-outputs'),
  screens: byId('screens'),
  tileTemplate: byId('tile-template'),
  stats: {
    video: byId('video-stats'),
    audio: byId('audio-stats'),
    connection: byId('connection-stats'),
  },
  control: {
    recording: { state: byId('recording-state'), detail: byId('recording-detail'), start: byId('recording-start'), pause: byId('recording-pause'), stop: byId('recording-stop') },
    streaming: { state: byId('streaming-state'), detail: byId('streaming-detail'), start: byId('streaming-start'), pause: byId('streaming-pause'), stop: byId('streaming-stop') },
  },
};

/** The current connection, or null. Handlers compare against it to ignore events from an old one. */
let session = null;

/** Whether the person has asked for silence; applies to both playback paths. */
let muted = false;

/** The tile of each stream shown in this window, by stream id. */
const tiles = new Map();

/** Streams the person has chosen not to watch in this window. */
const deselected = new Set();

/** Streams shown in windows of their own, by stream id. */
const popouts = new Map();

/** Value cells of the statistics panels, by panel and label. */
const statCells = {};

let screenListKey = '';
let audioListKey = '';

const DEFAULT_DEVICE_IDS = ['default-input', 'default-output'];

// ---------------------------------------------------------------------------------------------
// Page chrome
// ---------------------------------------------------------------------------------------------

function buildStatPanels() {
  for (const [panel, labels] of Object.entries(STAT_FIELDS)) {
    statCells[panel] = {};
    for (const label of labels) {
      const term = document.createElement('dt');
      term.textContent = label;
      const value = document.createElement('dd');
      value.textContent = EMPTY;
      el.stats[panel].append(term, value);
      statCells[panel][label] = value;
    }
  }
}

function showStats(panel, values) {
  for (const label of STAT_FIELDS[panel]) {
    const text = values?.[label] ?? EMPTY;
    const cell = statCells[panel][label];
    if (cell.textContent !== text) {
      cell.textContent = text;
    }
  }
}

function clearStats() {
  for (const panel of Object.keys(STAT_FIELDS)) {
    showStats(panel, null);
  }
  el.level.value = 0;
}

function setStatus(state, text) {
  el.status.dataset.state = state;
  el.status.textContent = text;
}

function log(message, kind = 'info') {
  const item = document.createElement('li');
  item.dataset.kind = kind;

  const time = document.createElement('time');
  const now = new Date();
  time.textContent = `${now.toLocaleTimeString([], { hour12: false })}.${String(now.getMilliseconds()).padStart(3, '0')}`;

  const text = document.createElement('span');
  text.textContent = message;

  item.append(time, text);
  el.log.append(item);
  while (el.log.childElementCount > MAX_LOG_LINES) {
    el.log.firstElementChild.remove();
  }
  el.log.scrollTop = el.log.scrollHeight;
}

function updatePlaybackControls() {
  const active = session !== null;
  el.mute.disabled = !active;
  el.fullscreen.disabled = !active || tiles.size === 0;
  el.mute.textContent = muted ? 'Unmute' : 'Mute';
  el.fullscreen.textContent = document.fullscreenElement === el.stage ? 'Exit full screen' : 'Full screen';
  el.connect.textContent = active ? 'Disconnect' : 'Connect';
  for (const tile of tiles.values()) {
    tile.updateFullscreenButton();
  }
}

function toggleFullscreen(element) {
  if (document.fullscreenElement) {
    document.exitFullscreen();
  } else {
    element.requestFullscreen();
  }
}

// ---------------------------------------------------------------------------------------------
// Formatting
// ---------------------------------------------------------------------------------------------

function formatBitRate(bitsPerSecond) {
  if (bitsPerSecond === undefined) {
    return EMPTY;
  }
  return bitsPerSecond >= 1_000_000
    ? `${(bitsPerSecond / 1_000_000).toFixed(2)} Mbit/s`
    : `${Math.round(bitsPerSecond / 1000)} kbit/s`;
}

function formatMilliseconds(seconds) {
  return seconds === undefined ? EMPTY : `${(seconds * 1000).toFixed(1)} ms`;
}

function formatBytes(bytes) {
  if (bytes === undefined) {
    return EMPTY;
  }
  return bytes >= 1_048_576 ? `${(bytes / 1_048_576).toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`;
}

function formatDuration(milliseconds) {
  const total = Math.floor(milliseconds / 1000);
  const hours = Math.floor(total / 3600);
  const minutes = String(Math.floor((total % 3600) / 60)).padStart(2, '0');
  const seconds = String(total % 60).padStart(2, '0');
  return hours > 0 ? `${hours}:${minutes}:${seconds}` : `${minutes}:${seconds}`;
}

function formatCount(value) {
  return value === undefined ? EMPTY : value.toLocaleString();
}

/** "video/H264" + "profile-level-id=42e01f" -> "H.264 Constrained Baseline". */
function describeCodec(codec) {
  if (!codec?.mimeType) {
    return EMPTY;
  }

  const name = codec.mimeType.split('/')[1];
  if (name.toUpperCase() === 'H264') {
    const match = /profile-level-id=([0-9a-f]{6})/i.exec(codec.sdpFmtpLine ?? '');
    if (!match) {
      return 'H.264';
    }
    const profileIdc = parseInt(match[1].slice(0, 2), 16);
    const constraints = parseInt(match[1].slice(2, 4), 16);
    const profile = { 66: 'Baseline', 77: 'Main', 100: 'High' }[profileIdc] ?? `profile ${profileIdc}`;
    const constrained = profileIdc === 66 && (constraints & 0x40) !== 0;
    return `H.264 ${constrained ? 'Constrained ' : ''}${profile}`;
  }

  const rate = codec.clockRate ? `, ${codec.clockRate / 1000} kHz` : '';
  const friendly = { PCMU: 'G.711 µ-law', PCMA: 'G.711 A-law' }[name.toUpperCase()];
  return friendly ? `${friendly}${rate}` : `${name}${rate}`;
}

function sourceById(id) {
  return session?.state?.sources.find((source) => source.id === id) ?? null;
}

function sourceName(id) {
  return sourceById(id)?.name ?? id;
}

// ---------------------------------------------------------------------------------------------
// Zoom: mouse wheel zooms around the pointer, dragging moves, double-click resets
// ---------------------------------------------------------------------------------------------

class Zoom {
  constructor(view, targets, onChange) {
    this.view = view;
    this.targets = targets;
    this.onChange = onChange;
    this.scale = 1;
    this.x = 0;
    this.y = 0;
    this.drag = null;

    view.addEventListener('wheel', (event) => this.onWheel(event), { passive: false });
    view.addEventListener('pointerdown', (event) => this.onPointerDown(event));
    view.addEventListener('pointermove', (event) => this.onPointerMove(event));
    view.addEventListener('pointerup', (event) => this.onPointerUp(event));
    view.addEventListener('pointercancel', (event) => this.onPointerUp(event));
    view.addEventListener('dblclick', () => this.reset());
    this.resizeObserver = new ResizeObserver(() => this.apply());
    this.resizeObserver.observe(view);
  }

  onWheel(event) {
    event.preventDefault(); // the wheel zooms the picture here instead of scrolling the page
    const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? this.view.clientHeight : 1;
    const factor = Math.exp(-event.deltaY * unit * 0.002);
    const rect = this.view.getBoundingClientRect();
    this.zoomAt(this.scale * factor, event.clientX - rect.left, event.clientY - rect.top);
  }

  /** Zooms to `scale`, keeping the picture point under (px, py) where it is. */
  zoomAt(scale, px, py) {
    const next = Math.min(MAX_ZOOM, Math.max(1, scale));
    this.x = px - ((px - this.x) * next) / this.scale;
    this.y = py - ((py - this.y) * next) / this.scale;
    this.scale = next;
    this.apply();
  }

  reset() {
    this.scale = 1;
    this.x = 0;
    this.y = 0;
    this.apply();
  }

  /** Keeps the picture covering the view, then applies the transform. */
  apply() {
    const width = this.view.clientWidth;
    const height = this.view.clientHeight;
    this.x = Math.min(0, Math.max(width - width * this.scale, this.x));
    this.y = Math.min(0, Math.max(height - height * this.scale, this.y));
    const transform = this.scale === 1 ? '' : `translate(${this.x}px, ${this.y}px) scale(${this.scale})`;
    for (const target of this.targets) {
      target.style.transform = transform;
    }
    this.view.classList.toggle('zoomed', this.scale > 1);
    this.onChange(this.scale);
  }

  onPointerDown(event) {
    if (event.button !== 0 || this.scale === 1) {
      return;
    }
    this.drag = { id: event.pointerId, x: event.clientX, y: event.clientY };
    this.view.setPointerCapture(event.pointerId);
    this.view.classList.add('dragging');
  }

  onPointerMove(event) {
    if (this.drag?.id !== event.pointerId) {
      return;
    }
    this.x += event.clientX - this.drag.x;
    this.y += event.clientY - this.drag.y;
    this.drag.x = event.clientX;
    this.drag.y = event.clientY;
    this.apply();
  }

  onPointerUp(event) {
    if (this.drag?.id === event.pointerId) {
      this.drag = null;
      this.view.classList.remove('dragging');
    }
  }

  dispose() {
    this.resizeObserver.disconnect();
  }
}

// ---------------------------------------------------------------------------------------------
// Tiles: one per stream shown in this window
// ---------------------------------------------------------------------------------------------

class Tile {
  constructor(source) {
    this.element = el.tileTemplate.content.firstElementChild.cloneNode(true);
    this.element.dataset.source = source.id;
    this.id = source.id;
    this.name = this.element.querySelector('.tile-name');
    this.info = this.element.querySelector('.tile-info');
    this.zoomButton = this.element.querySelector('.tile-zoom');
    this.fullscreenButton = this.element.querySelector('.tile-fullscreen');
    this.popoutButton = this.element.querySelector('.tile-popout');
    this.view = this.element.querySelector('.tile-view');
    this.video = this.element.querySelector('video');
    this.canvas = this.element.querySelector('canvas');
    this.wait = this.element.querySelector('.tile-wait');
    this.track = null;
    this.receiving = false;

    this.zoom = new Zoom(this.view, [this.video, this.canvas], (scale) => {
      this.zoomButton.textContent = `${Math.round(scale * 100)}%`;
    });
    this.zoomButton.addEventListener('click', () => this.zoom.reset());
    this.fullscreenButton.addEventListener('click', () => toggleFullscreen(this.element));

    if (POPOUT) {
      this.popoutButton.textContent = 'Close';
      this.popoutButton.title = 'Close this window. The screen goes back to the main window.';
      this.popoutButton.addEventListener('click', () => window.close());
    } else {
      this.popoutButton.title = 'Show this screen in a window of its own';
      this.popoutButton.addEventListener('click', () => popOut(this.id));
    }

    this.video.addEventListener('resize', () => {
      if (this.video.videoWidth > 0) {
        this.markReceiving();
      }
    });

    this.update(source);
  }

  update(source) {
    this.source = source;
    this.name.textContent = source.primary ? `${source.name} (primary)` : source.name;
    this.view.style.setProperty('--aspect', `${source.videoWidth} / ${source.videoHeight}`);
  }

  setMode(jpeg) {
    this.video.hidden = jpeg;
    this.canvas.hidden = !jpeg;
  }

  attach(track) {
    if (this.track === track) {
      return;
    }
    this.track = track;
    this.video.srcObject = track ? new MediaStream([track]) : null;
    if (track) {
      this.video.play().catch(() => {}); // muted video may always play
    } else {
      this.setReceiving(false);
    }
  }

  draw(bitmap) {
    if (this.canvas.width !== bitmap.width || this.canvas.height !== bitmap.height) {
      this.canvas.width = bitmap.width;
      this.canvas.height = bitmap.height;
    }
    this.canvas.getContext('2d').drawImage(bitmap, 0, 0);
    this.markReceiving();
  }

  clearPicture() {
    this.canvas.getContext('2d').clearRect(0, 0, this.canvas.width, this.canvas.height);
    this.setReceiving(false);
  }

  markReceiving() {
    if (!this.receiving) {
      this.setReceiving(true);
      const s = session;
      if (s) {
        log(`${this.source.name}: first picture after ${Math.round(performance.now() - s.mediaStartedAt)} ms`, 'good');
      }
    }
  }

  setReceiving(receiving) {
    this.receiving = receiving;
    this.wait.hidden = receiving;
  }

  setInfo(text) {
    if (this.info.textContent !== text) {
      this.info.textContent = text;
    }
  }

  updateFullscreenButton() {
    this.fullscreenButton.textContent = document.fullscreenElement === this.element ? 'Exit full screen' : 'Full screen';
  }

  destroy() {
    this.attach(null);
    this.zoom.dispose();
    this.element.remove();
  }
}

/** The streams this window shows: everything ticked that is not in a window of its own, or the one stream of a pop-out. */
function wantedSources() {
  const sources = session?.state?.sources ?? [];
  if (POPOUT) {
    return sources.filter((source) => source.id === POPOUT);
  }
  return sources
    .filter((source) => !deselected.has(source.id) && !popouts.has(source.id))
    .sort((a, b) => a.left - b.left || a.top - b.top); // as the screens are arranged
}

/** Brings the tiles in line with what should be shown, then tells the capture client. */
function renderTiles() {
  const s = session;
  const state = s?.state;
  const wanted = state && state.streaming.state !== 'stopped' ? wantedSources() : [];
  const wantedIds = new Set(wanted.map((source) => source.id));

  for (const [id, tile] of tiles) {
    if (!wantedIds.has(id)) {
      tile.destroy();
      tiles.delete(id);
    }
  }

  const jpeg = state?.video.mode === 'jpeg';
  for (const source of wanted) {
    let tile = tiles.get(source.id);
    if (tile) {
      tile.update(source);
    } else {
      tile = new Tile(source);
      tiles.set(source.id, tile);
    }
    tile.setMode(jpeg);
    tile.attach(jpeg ? null : s.tracks.get(source.id) ?? null);
  }

  // Re-append only when the order is wrong: moving a playing video element can interrupt it.
  const order = wanted.map((source) => tiles.get(source.id).element);
  if (order.some((element, index) => el.tiles.children[index] !== element)) {
    el.tiles.append(...order);
  }
  el.tiles.style.setProperty('--columns', wanted.length <= 1 ? 1 : wanted.length <= 4 ? 2 : 3);

  updatePlaceholder();
  updatePlaybackControls();
  renderScreenList();
  sendSelection();
}

function updatePlaceholder() {
  const s = session;
  const state = s?.state;
  let title = '';
  let text = '';

  if (!s) {
    title = 'Not connected';
    text = 'Start the capture client, then press Connect.';
  } else if (!state) {
    title = 'Connecting…';
  } else if (state.streaming.state === 'stopped') {
    title = 'Streaming is stopped';
    text = state.remoteControl ? 'Press Start under Streaming to watch.' : 'It has to be started on the capture client.';
  } else if (tiles.size === 0) {
    if (POPOUT) {
      title = `${sourceName(POPOUT)} is no longer captured`;
      text = 'The capture client changed its layout. Close this window.';
    } else if (state.sources.length > 0 && state.sources.every((source) => popouts.has(source.id))) {
      title = 'Every screen is in its own window';
      text = 'Close one of those windows, or press Bring back under Watch.';
    } else {
      title = 'No screen selected';
      text = 'Tick a screen under Watch.';
    }
  }

  el.placeholder.hidden = s !== null && state !== null && tiles.size > 0;
  el.placeholderTitle.textContent = title;
  el.placeholderText.textContent = text;
}

/** Tells the capture client which streams this window wants, so it is sent (and has to encode) nothing else. */
function sendSelection() {
  const s = session;
  if (!s?.state || s.socket.readyState !== WebSocket.OPEN) {
    return;
  }
  const message = { type: 'select', sources: [...tiles.keys()].sort(), audio: !POPOUT };
  const key = JSON.stringify(message);
  if (key !== s.lastSelection) {
    s.lastSelection = key;
    send(message);
  }
}

/** The list under Watch: a tick box and a pop-out button per stream. */
function renderScreenList() {
  if (POPOUT) {
    return;
  }

  const sources = session?.state?.sources ?? [];
  const key = JSON.stringify([sources.map((s) => [s.id, s.name, s.width, s.height, s.primary]), [...deselected], [...popouts.keys()]]);
  if (key === screenListKey) {
    return;
  }
  screenListKey = key;

  if (sources.length === 0) {
    const empty = document.createElement('li');
    empty.className = 'empty';
    empty.textContent = session ? 'The capture client offers no screens.' : 'Connect to see the screens.';
    el.screens.replaceChildren(empty);
    return;
  }

  el.screens.replaceChildren(...sources.map((source) => {
    const item = document.createElement('li');
    const poppedOut = popouts.has(source.id);

    const label = document.createElement('label');
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.dataset.source = source.id;
    box.checked = !deselected.has(source.id) && !poppedOut;
    box.disabled = poppedOut;
    box.addEventListener('change', () => {
      if (box.checked) {
        deselected.delete(source.id);
      } else {
        deselected.add(source.id);
      }
      renderTiles();
    });
    const name = document.createElement('span');
    name.className = 'screen-name';
    name.textContent = source.name;
    label.append(box, name);

    const button = document.createElement('button');
    button.type = 'button';
    button.dataset.source = source.id;
    button.textContent = poppedOut ? 'Bring back' : 'Pop out';
    button.addEventListener('click', () => (poppedOut ? bringBack(source.id) : popOut(source.id)));

    const detail = document.createElement('span');
    detail.className = 'screen-detail';
    const parts = [`${source.width} × ${source.height}`];
    if (source.primary) {
      parts.push('primary');
    }
    if (source.screens.length > 1) {
      parts.push(`screens ${source.screens.join(', ')}`);
    }
    if (poppedOut) {
      parts.push('in its own window');
    }
    detail.textContent = parts.join(' · ');

    item.append(label, button, detail);
    return item;
  }));
}

// ---------------------------------------------------------------------------------------------
// Pop-out windows
// ---------------------------------------------------------------------------------------------

function popOut(id) {
  const s = session;
  const source = sourceById(id);
  if (!s || !source) {
    return;
  }

  const existing = popouts.get(id);
  if (existing && !existing.closed) {
    existing.focus();
    return;
  }

  const width = Math.min(1280, Math.round(screen.availWidth * 0.6));
  const height = Math.round((width * source.videoHeight) / source.videoWidth) + 40;
  const url = new URL(location.pathname, location.href);
  url.searchParams.set('popout', id);
  url.searchParams.set('url', s.url);
  url.searchParams.set('autoconnect', '');

  const win = window.open(url.href, `capture-${id}`, `popup,width=${width},height=${height}`);
  if (!win) {
    log(`The browser blocked the window for ${source.name}. Allow pop-ups for this page and try again.`, 'error');
    return;
  }

  popouts.set(id, win);
  log(`${source.name} is in a window of its own`);
  renderTiles();
}

function bringBack(id) {
  const win = popouts.get(id);
  popouts.delete(id);
  try {
    win?.close();
  } catch {
    // Already gone.
  }
  log(`${sourceName(id)} is back in this window`);
  renderTiles();
}

function closePopouts() {
  for (const win of popouts.values()) {
    try {
      win.close();
    } catch {
      // Already gone.
    }
  }
  popouts.clear();
}

// A pop-out that is closed (by its own button, the window's close box or anything else) returns its screen here.
setInterval(() => {
  let changed = false;
  for (const [id, win] of popouts) {
    if (win.closed) {
      popouts.delete(id);
      log(`${sourceName(id)} is back in this window`);
      changed = true;
    }
  }
  if (changed) {
    renderTiles();
  }
}, 500);

// ---------------------------------------------------------------------------------------------
// PCM playback (JPEG mode)
// ---------------------------------------------------------------------------------------------

/**
 * Plays blocks of 16-bit 8 kHz PCM as they arrive. Each block is scheduled to start exactly where
 * the previous one ends, a little behind the moment of arrival so that late blocks leave no gap.
 */
class PcmPlayer {
  constructor() {
    try {
      // A context running at the stream's own rate plays consecutive blocks sample-exactly.
      this.context = new AudioContext({ sampleRate: PCM_SAMPLE_RATE, latencyHint: 'interactive' });
    } catch {
      this.context = new AudioContext({ latencyHint: 'interactive' });
    }
    this.gain = this.context.createGain();
    this.gain.connect(this.context.destination);
    this.nextTime = 0;
    this.setMuted(muted);
  }

  get suspended() {
    return this.context.state === 'suspended';
  }

  /** Seconds of audio scheduled but not yet played. */
  get buffered() {
    return Math.max(0, this.nextTime - this.context.currentTime);
  }

  play(samples) {
    const buffer = this.context.createBuffer(1, samples.length, PCM_SAMPLE_RATE);
    const channel = buffer.getChannelData(0);
    for (let i = 0; i < samples.length; i++) {
      channel[i] = samples[i] / 32768;
    }

    const now = this.context.currentTime;
    if (this.nextTime < now + 0.02) {
      this.nextTime = now + 0.1; // starting, or caught up with the data: put 100 ms in hand
    } else if (this.nextTime > now + 0.6) {
      return; // far too much queued (the tab was throttled, or clocks differ): drop to catch up
    }

    const source = this.context.createBufferSource();
    source.buffer = buffer;
    source.connect(this.gain);
    source.start(this.nextTime);
    this.nextTime += buffer.duration;
  }

  setMuted(value) {
    this.gain.gain.value = value ? 0 : 1;
  }

  resume() {
    return this.context.resume();
  }

  close() {
    this.context.close().catch(() => {});
  }
}

// ---------------------------------------------------------------------------------------------
// Connection
// ---------------------------------------------------------------------------------------------

/** A pop-out asks for its one stream and no audio as it connects, so it is never sent anything else. */
function socketUrl(url) {
  if (!POPOUT) {
    return url;
  }
  const withSelection = new URL(url);
  withSelection.searchParams.set('select', POPOUT);
  withSelection.searchParams.set('audio', '0');
  return withSelection.href;
}

function newPictureCounters() {
  return { decoding: false, pending: null, received: 0, shown: 0, skipped: 0, bytes: 0, width: 0, height: 0 };
}

function connect() {
  const url = el.url.value.trim();
  if (!/^wss?:\/\/\S+$/i.test(url)) {
    setStatus('error', 'The address has to start with ws:// or wss://');
    el.url.focus();
    return;
  }

  disconnect();
  if (!POPOUT) {
    try {
      localStorage.setItem(URL_STORAGE_KEY, url);
    } catch {
      // Storage can be unavailable (private window, file:// page); the address just is not remembered.
    }
  }

  const s = {
    url,
    socket: null,
    socketOpened: false,
    mediaStartedAt: performance.now(),
    openedAt: 0,
    bytesReceived: 0,
    state: null,
    lastSelection: '',
    audioPending: null,       // {ids, until}: an audio device change sent but not yet confirmed
    audioError: null,         // why the last audio device change was refused
    // WebRTC (h264 mode)
    peer: null,
    signaling: Promise.resolve(),
    earlyCandidates: [],
    tracks: new Map(),        // stream id -> video track
    trackToSource: new Map(), // track id -> stream id
    peerConnectedAt: 0,
    // WebSocket media (jpeg mode)
    pictures: new Map(),      // stream id -> counters
    pcm: null,
    audio: { blocks: 0, samples: 0, bytes: 0, level: 0 },
    statsTimer: 0,
    previous: {},
    snapshot: { state: 'connecting' },
  };

  try {
    s.socket = new WebSocket(socketUrl(url));
  } catch (error) {
    setStatus('error', `That address cannot be used: ${error.message}`);
    return;
  }

  s.socket.binaryType = 'arraybuffer';
  session = s;
  setStatus('connecting', 'Connecting…');
  updatePlaceholder();
  updatePlaybackControls();
  log(`Connecting to ${url}`);

  s.socket.onopen = () => {
    s.socketOpened = true;
    s.openedAt = performance.now();
    log('Connected to the capture client', 'good');
    refreshStatus(s);
  };

  s.socket.onmessage = (event) => {
    if (session !== s) {
      return;
    }
    if (typeof event.data === 'string') {
      s.bytesReceived += event.data.length;
      // Text messages are handled strictly one after another: a candidate must not be applied
      // before the offer that precedes it has finished being set.
      s.signaling = s.signaling.then(() => handleMessage(s, event.data));
    } else {
      s.bytesReceived += event.data.byteLength;
      handleMedia(s, event.data);
    }
  };

  s.socket.onclose = () => {
    if (session !== s) {
      return;
    }
    if (!s.socketOpened) {
      fail(s, `Cannot reach ${url}. Check that the capture client is running and that it accepts pages from ${location.origin}.`);
    } else {
      log('The capture client closed the connection');
      disconnect();
      setStatus('idle', 'Disconnected: the capture client stopped');
    }
  };

  s.statsTimer = setInterval(() => pollStats(s), STATS_INTERVAL_MS);
}

function send(message) {
  if (session?.socket.readyState === WebSocket.OPEN) {
    session.socket.send(JSON.stringify(message));
  }
}

async function handleMessage(s, data) {
  if (session !== s) {
    return;
  }

  let message;
  try {
    message = JSON.parse(data);
  } catch {
    log('Ignored a message that is not JSON', 'error');
    return;
  }

  try {
    if (message.type === 'state') {
      applyState(s, message);
    } else if (message.type === 'error') {
      log(message.message, 'error');
      if (s.audioPending) {
        // The refusal of the audio change just asked for: the list goes back to what is recorded.
        s.audioPending = null;
        s.audioError = message.message;
        renderAudioDevices(s.state);
      } else {
        showControlNote(message.message, 'error');
      }
    } else if (message.sdp) {
      if (message.type === 'offer') {
        await acceptOffer(s, message);
      }
    } else if (message.candidate) {
      if (s.peer?.remoteDescription) {
        await s.peer.addIceCandidate(message);
      } else {
        s.earlyCandidates.push(message); // a candidate cannot be applied before the offer it belongs to
      }
    }
  } catch (error) {
    log(`A message from the capture client could not be handled: ${error.message}`, 'error');
  }
}

function fail(s, message) {
  if (session !== s) {
    return;
  }
  log(message, 'error');
  disconnect();
  setStatus('error', message);
}

function disconnect() {
  const s = session;
  if (s === null) {
    return;
  }

  session = null;
  clearInterval(s.statsTimer);
  try {
    s.socket.close();
  } catch {
    // Already closed.
  }
  closePeer(s);
  closePictures(s);
  closePopouts();

  renderTiles();
  el.pausedBadge.hidden = true;
  el.unmute.hidden = true;
  clearStats();
  showControls(null);
  updatePlaybackControls();
}

/** One line under the page title that says what is going on right now. */
function refreshStatus(s) {
  if (session !== s) {
    return;
  }

  const state = s.state;
  if (!s.socketOpened) {
    setStatus('connecting', 'Connecting…');
  } else if (!state) {
    setStatus('connecting', 'Connected, waiting for the capture client…');
  } else if (state.streaming.state === 'stopped') {
    setStatus('idle', 'Connected · streaming is stopped');
  } else if (state.streaming.state === 'paused') {
    setStatus('idle', 'Connected · streaming is paused');
  } else if (state.video.mode === 'jpeg') {
    const live = [...s.pictures.values()].some((counters) => counters.shown > 0);
    setStatus(live ? 'live' : 'connecting', live ? 'Live · JPEG' : 'Waiting for pictures…');
  } else {
    const connected = s.peer?.connectionState === 'connected';
    setStatus(connected ? 'live' : 'connecting', connected ? 'Live · H.264' : 'Starting the stream…');
  }
}

// ---------------------------------------------------------------------------------------------
// State and remote control
// ---------------------------------------------------------------------------------------------

/** Brings the page in line with what the capture client says it is doing. */
function applyState(s, state) {
  const previous = s.state;
  s.state = state;

  const streaming = state.streaming.state;
  const mode = state.video.mode;
  const sourceIds = state.sources.map((source) => source.id).join(',');

  if (!previous || previous.streaming.state !== streaming) {
    log(`Streaming is ${streaming}`);
  }
  if (!previous || previous.video.mode !== mode) {
    log(`Video method: ${mode === 'jpeg' ? 'JPEG pictures over the WebSocket' : 'H.264 over WebRTC'}`);
  }
  if (!previous || previous.sources.map((source) => source.id).join(',') !== sourceIds) {
    log(`Streams: ${state.sources.map((source) => `${source.name} (${source.videoWidth} × ${source.videoHeight})`).join(', ') || 'none'}`);

    // Forget choices about streams that no longer exist, and close their windows.
    for (const id of [...deselected]) {
      if (!state.sources.some((source) => source.id === id)) {
        deselected.delete(id);
      }
    }
    for (const [id, win] of [...popouts]) {
      if (!state.sources.some((source) => source.id === id)) {
        popouts.delete(id);
        try {
          win.close();
        } catch {
          // Already gone.
        }
      }
    }
  }
  if (previous && previous.layout !== state.layout) {
    log(`Layout: ${state.layout === 'desktop' ? 'the whole desktop as one stream' : 'each screen separately'}`);
  }
  if (previous && previous.recording.state !== state.recording.state) {
    log(`Recording is ${state.recording.state}${state.recording.files.length ? ` (${state.recording.files.join(', ')})` : ''}`);
  }
  if (previous && previous.video.frameRate !== state.video.frameRate) {
    log(`Frame rate is ${state.video.frameRate} fps`);
  }
  if (!previous || !sameMembers(previous.audio.selected, state.audio.selected)) {
    if (state.audio.enabled) {
      log(`Audio from: ${describeAudioSelection(state.audio)}`);
    }
  }
  if (s.audioPending && (sameMembers(s.audioPending.ids, state.audio.selected) || performance.now() > s.audioPending.until)) {
    s.audioPending = null;
  }

  if (!previous || previous.streaming.state !== streaming || previous.video.mode !== mode || previous.sources.map((source) => source.id).join(',') !== sourceIds) {
    s.mediaStartedAt = performance.now();
  }

  // Each transport only lives while the capture client is using it.
  if (streaming === 'stopped' || mode !== 'h264') {
    closePeer(s);
  }
  if (streaming === 'stopped' || mode !== 'jpeg') {
    closePictures(s);
  }

  if (POPOUT) {
    const source = state.sources.find((candidate) => candidate.id === POPOUT);
    document.title = `${source?.name ?? POPOUT} – Capture Viewer`;
  }

  el.pausedBadge.hidden = streaming !== 'paused';
  renderTiles();
  showControls(state);
  refreshStatus(s);
}

function showControlNote(text, kind = 'info') {
  el.controlNote.textContent = text;
  el.controlNote.dataset.kind = kind;
  el.controlNote.hidden = text === '';
}

function describeRecording(recording) {
  if (recording.state === 'stopped') {
    return 'Not recording';
  }
  const files = recording.files.length === 0 ? 'starting…'
    : recording.files.length === 1 ? recording.files[0]
    : `${recording.files[0]} + ${recording.files.length - 1} more`;
  return [files, formatDuration(recording.seconds * 1000), formatBytes(recording.bytes)].join(' · ');
}

function describeStreaming(state) {
  const { streaming, video } = state;
  const method = video.mode === 'jpeg' ? 'JPEG' : 'H.264';
  if (streaming.state === 'stopped') {
    return `${streaming.clients} connected`;
  }
  return `${method} · ${streaming.viewers} watching of ${streaming.clients} connected`;
}

/** Shows a state in the remote-control panel; null means not connected. */
function showControls(state) {
  const enabled = state !== null && state.remoteControl;

  for (const target of ['recording', 'streaming']) {
    const row = el.control[target];
    const current = state?.[target].state ?? 'unknown';
    row.state.dataset.state = current;
    row.state.textContent = state ? current : EMPTY;
    row.detail.textContent = !state ? ' ' : target === 'recording' ? describeRecording(state.recording) : describeStreaming(state);
    row.start.disabled = !enabled || current !== 'stopped';
    row.pause.disabled = !enabled || current === 'stopped';
    row.pause.textContent = current === 'paused' ? 'Resume' : 'Pause';
    row.stop.disabled = !enabled || current === 'stopped';
  }

  // Leave a field alone while the person is editing it.
  const setValue = (field, value) => {
    if (document.activeElement !== field && String(field.value) !== String(value)) {
      field.value = value;
    }
  };

  el.layout.disabled = !enabled;
  el.videoMode.disabled = !enabled;
  el.frameRate.disabled = !enabled;

  if (state) {
    setValue(el.layout, state.layout);
    setValue(el.videoMode, state.video.mode);
    setValue(el.frameRate, state.video.frameRate);
  }

  if (!state) {
    showControlNote('Connect to control the capture client.');
  } else if (!state.remoteControl) {
    showControlNote('Remote control is turned off on the capture client.');
  } else if (el.controlNote.dataset.kind !== 'error') {
    showControlNote('');
  }

  renderAudioDevices(state);
}

// ---------------------------------------------------------------------------------------------
// Audio devices: a tick box for each device the capture client can record
// ---------------------------------------------------------------------------------------------

function sameMembers(a = [], b = []) {
  return a.length === b.length && a.every((item) => b.includes(item));
}

function isDefaultEntry(device) {
  return DEFAULT_DEVICE_IDS.includes(device.id);
}

/** What is selected as far as this page knows: a change it asked for counts before the capture client confirms it. */
function audioSelection(s) {
  return s?.audioPending?.ids ?? s?.state?.audio?.selected ?? [];
}

/** "the default recording device (Microphone (USB Audio))", or the device's name. */
function audioDeviceLabel(device) {
  if (isDefaultEntry(device)) {
    const kind = device.kind === 'input' ? 'recording' : 'playback';
    return device.device ? `the default ${kind} device (${device.device})` : `the default ${kind} device`;
  }
  return device.name;
}

function describeAudioSelection(audio) {
  const names = audio.selected.map((id) => {
    const device = audio.devices.find((candidate) => candidate.id === id);
    return device ? audioDeviceLabel(device) : id;
  });
  return names.length > 0 ? names.join(' + ') : 'nothing (silence)';
}

/** The line under a device's name: which device a default entry is, and how a selected device is doing. */
function describeAudioDevice(device, selected) {
  const parts = [];
  if (isDefaultEntry(device)) {
    parts.push(device.device ?? `there is no ${device.kind === 'input' ? 'recording' : 'playback'} device`);
  } else {
    if (device.default) {
      parts.push('Windows default');
    }
    if (!device.present) {
      parts.push('not connected');
    }
  }

  if (device.status === 'capturing') {
    parts.push(device.format ? `live · ${device.format}` : 'live');
  } else if (selected && device.status === 'starting') {
    parts.push('starting…');
  } else if (selected && device.status === 'idle') {
    parts.push('idle: nothing is recorded or watched');
  } else if (selected && device.status === 'unavailable') {
    parts.push(device.problem ?? 'unavailable');
  }
  return parts.join(' · ');
}

function audioDeviceItem(device, selected, canChange) {
  const item = document.createElement('li');
  item.dataset.status = selected || device.status === 'capturing' ? device.status : 'off';
  item.dataset.present = String(device.present);

  const label = document.createElement('label');
  const box = document.createElement('input');
  box.type = 'checkbox';
  box.dataset.device = device.id;
  box.checked = selected;
  box.disabled = !canChange || (!selected && !device.present);
  box.addEventListener('change', () => chooseAudioDevice(device, box.checked));

  const name = document.createElement('span');
  name.className = isDefaultEntry(device) ? 'device-name stand-in' : 'device-name';
  name.textContent = isDefaultEntry(device) ? 'Default device' : device.name;
  label.append(box, name);

  const detail = document.createElement('span');
  detail.className = 'device-detail';
  detail.textContent = describeAudioDevice(device, selected);

  item.append(label, detail);
  return item;
}

/** The list under Audio devices: recording devices, then playback devices, each with the default first. */
function renderAudioDevices(state) {
  const s = session;
  const audio = state?.audio;
  const canChange = Boolean(state?.remoteControl && audio?.enabled);
  const selected = new Set(audioSelection(s));
  const devices = audio?.enabled ? audio.devices ?? [] : [];

  let note = '';
  let kind = 'info';
  if (!state) {
    note = 'Connect to see the audio devices.';
  } else if (!audio.enabled) {
    note = 'Audio is turned off on the capture client.';
  } else if (s?.audioError) {
    note = s.audioError;
    kind = 'error';
  } else if (!state.remoteControl) {
    note = 'Remote control is turned off on the capture client, so these cannot be changed here.';
  } else if (selected.size === 0) {
    note = 'Nothing is ticked: the audio is silence.';
  }
  el.audioNote.textContent = note;
  el.audioNote.dataset.kind = kind;
  el.audioNote.hidden = note === '';
  el.audioGroups.hidden = devices.length === 0;

  const key = JSON.stringify([canChange, [...selected], devices]);
  if (key === audioListKey) {
    return;
  }
  audioListKey = key;

  // The list is rebuilt; keep the keyboard focus on the device that had it.
  const focused = el.audioDevices.contains(document.activeElement) ? document.activeElement.dataset.device : undefined;
  el.audioInputs.replaceChildren(...devices.filter((device) => device.kind === 'input').map((device) => audioDeviceItem(device, selected.has(device.id), canChange)));
  el.audioOutputs.replaceChildren(...devices.filter((device) => device.kind === 'output').map((device) => audioDeviceItem(device, selected.has(device.id), canChange)));
  if (focused !== undefined) {
    [...el.audioDevices.querySelectorAll('input')].find((box) => box.dataset.device === focused)?.focus();
  }
}

/** Asks the capture client to record, or stop recording, one device; the others stay as they are. */
function chooseAudioDevice(device, record) {
  const s = session;
  if (!s?.state) {
    return;
  }

  const ids = audioSelection(s).filter((id) => id !== device.id);
  if (record) {
    ids.push(device.id);
  }

  // Until the capture client confirms, the page shows what was asked for, and a second click builds on it.
  s.audioPending = { ids, until: performance.now() + 3000 };
  s.audioError = null;
  log(`Request: ${record ? 'record' : 'stop recording'} ${audioDeviceLabel(device)}`);
  send({ type: 'configure', audioDevices: ids });
  renderAudioDevices(s.state);
}

function sendCommand(target, action) {
  showControlNote('');
  log(`Request: ${action} ${target}`);
  send({ type: 'command', target, action });
}

function configure(setting, value) {
  showControlNote('');
  log(`Request: ${setting} = ${value}`);
  send({ type: 'configure', [setting]: value });
}

// ---------------------------------------------------------------------------------------------
// H.264 mode: WebRTC
// ---------------------------------------------------------------------------------------------

async function acceptOffer(s, offer) {
  closePeer(s);

  const peer = new RTCPeerConnection();
  s.peer = peer;
  s.peerConnectedAt = 0;
  s.previous = {};
  const sourceByMid = new Map(Object.entries(offer.sources ?? {}));
  log(`Offer received (${sourceByMid.size} video stream${sourceByMid.size === 1 ? '' : 's'}${/^m=audio/m.test(offer.sdp) ? ' and audio' : ''})`);

  peer.onicecandidate = (event) => {
    if (event.candidate && s.peer === peer) {
      send(event.candidate);
    }
  };

  peer.onconnectionstatechange = () => {
    if (s.peer !== peer) {
      return;
    }
    const state = peer.connectionState;
    log(`WebRTC ${state}`, state === 'connected' ? 'good' : state === 'failed' ? 'error' : 'info');
    if (state === 'connected') {
      s.peerConnectedAt = performance.now();
    }
    refreshStatus(s);
  };

  await peer.setRemoteDescription({ type: 'offer', sdp: offer.sdp });

  // Each transceiver's mid says which stream its track carries; the tracks exist from here on and
  // start delivering pictures once the connection is up.
  for (const transceiver of peer.getTransceivers()) {
    const track = transceiver.receiver.track;
    if (track.kind === 'audio') {
      if (!POPOUT) {
        playAudio(track);
      }
    } else if (sourceByMid.has(transceiver.mid)) {
      const id = sourceByMid.get(transceiver.mid);
      s.tracks.set(id, track);
      s.trackToSource.set(track.id, id);
    }
    // A live view: ask for the smallest receive buffer the network allows.
    if ('jitterBufferTarget' in transceiver.receiver) {
      transceiver.receiver.jitterBufferTarget = 0;
    }
  }
  renderTiles();

  for (const candidate of s.earlyCandidates.splice(0)) {
    await peer.addIceCandidate(candidate);
  }
  await peer.setLocalDescription(await peer.createAnswer());
  if (s.peer === peer) {
    send(peer.localDescription);
  }
}

function closePeer(s) {
  if (!s.peer) {
    return;
  }
  s.peer.close();
  s.peer = null;
  s.earlyCandidates = [];
  s.tracks.clear();
  s.trackToSource.clear();
  el.audio.srcObject = null;
  for (const tile of tiles.values()) {
    tile.attach(null);
  }
}

async function playAudio(track) {
  el.audio.srcObject = new MediaStream([track]);
  el.audio.muted = muted;
  try {
    await el.audio.play();
  } catch (error) {
    if (error.name === 'NotAllowedError') {
      // The browser will not start sound without a click on the page; offer a button.
      el.unmute.hidden = false;
    }
    // An AbortError only means a newer play() call replaced this one.
  }
}

// ---------------------------------------------------------------------------------------------
// JPEG mode: pictures and PCM audio over the WebSocket
// ---------------------------------------------------------------------------------------------

function handleMedia(s, buffer) {
  if (buffer.byteLength < HEADER_LENGTH || s.state?.video.mode !== 'jpeg') {
    return;
  }

  const header = new DataView(buffer);
  const type = header.getUint8(0);
  if (type === PICTURE_MESSAGE) {
    const number = header.getUint8(1);
    const source = s.state.sources.find((candidate) => candidate.number === number);
    if (!source) {
      return; // a stream from before a layout change
    }
    let counters = s.pictures.get(source.id);
    if (!counters) {
      counters = newPictureCounters();
      s.pictures.set(source.id, counters);
    }
    counters.received++;
    counters.bytes += buffer.byteLength - HEADER_LENGTH;
    showPicture(s, source.id, counters, new Uint8Array(buffer, HEADER_LENGTH));
  } else if (type === AUDIO_MESSAGE && !POPOUT) {
    playPcm(s, new Int16Array(buffer.slice(HEADER_LENGTH)));
  }
}

async function showPicture(s, id, counters, jpeg) {
  if (counters.decoding) {
    // Still busy with an earlier picture: keep only the newest one waiting.
    if (counters.pending) {
      counters.skipped++;
    }
    counters.pending = jpeg;
    return;
  }

  counters.decoding = true;
  try {
    for (let next = jpeg; next; next = counters.pending, counters.pending = null) {
      const bitmap = await createImageBitmap(new Blob([next], { type: 'image/jpeg' }));
      const tile = tiles.get(id);
      if (session !== s || s.state?.video.mode !== 'jpeg' || !tile) {
        bitmap.close();
        return;
      }

      tile.draw(bitmap);
      counters.width = bitmap.width;
      counters.height = bitmap.height;
      bitmap.close();

      if (counters.shown++ === 0) {
        refreshStatus(s);
      }
    }
  } catch (error) {
    log(`A picture of ${sourceName(id)} could not be shown: ${error.message}`, 'error');
  } finally {
    counters.decoding = false;
  }
}

function playPcm(s, samples) {
  if (!s.pcm) {
    s.pcm = new PcmPlayer();
    el.unmute.hidden = !s.pcm.suspended;
  }

  s.audio.blocks++;
  s.audio.samples += samples.length;
  s.audio.bytes += samples.byteLength;

  let peak = 0;
  for (let i = 0; i < samples.length; i++) {
    peak = Math.max(peak, Math.abs(samples[i]));
  }
  s.audio.level = Math.max(peak / 32768, s.audio.level * 0.8);

  s.pcm.play(samples);
}

function closePictures(s) {
  if (!s.pcm && s.pictures.size === 0 && s.audio.blocks === 0) {
    return; // nothing was open
  }

  s.pcm?.close();
  s.pcm = null;
  s.pictures = new Map();
  s.audio = { blocks: 0, samples: 0, bytes: 0, level: 0 };
  s.previous = {};
  for (const tile of tiles.values()) {
    tile.clearPicture();
  }
}

// ---------------------------------------------------------------------------------------------
// Statistics
// ---------------------------------------------------------------------------------------------

/** Per-second rate of a counter, from the difference to the previous poll. */
function rateOf(s, key, value, timestamp) {
  const previous = s.previous[key];
  s.previous[key] = { value, timestamp };
  if (value === undefined || !previous || timestamp <= previous.timestamp) {
    return undefined;
  }
  return ((value - previous.value) * 1000) / (timestamp - previous.timestamp);
}

function describeCandidate(candidate) {
  if (!candidate) {
    return '?';
  }
  const address = candidate.address ?? candidate.ip;
  return address ? `${candidate.candidateType} ${address}:${candidate.port}` : candidate.candidateType;
}

/** Totals over the streams shown in this window, for the Video panel. */
function summarizeVideo(perSource) {
  const shown = [...tiles.keys()].map((id) => perSource[id]).filter(Boolean);
  if (shown.length === 0) {
    return null;
  }
  const sum = (key) => shown.reduce((total, stats) => total + (stats[key] ?? 0), 0);
  return {
    streams: shown.length,
    codec: shown[0].codec,
    framesPerSecond: Math.round((sum('framesPerSecond') / shown.length) * 10) / 10,
    bitsPerSecond: sum('bitsPerSecond'),
    framesDecoded: sum('framesDecoded'),
    framesDropped: sum('framesDropped'),
    keyFramesDecoded: shown[0].keyFramesDecoded === undefined ? undefined : sum('keyFramesDecoded'),
    packetsLost: shown[0].packetsLost === undefined ? undefined : sum('packetsLost'),
    jitterMs: shown[0].jitterMs === undefined ? undefined : Math.max(...shown.map((stats) => stats.jitterMs ?? 0)),
  };
}

function showVideoSummary(video) {
  showStats('video', video && {
    'Streams': String(video.streams),
    'Codec': video.codec,
    'Frame rate': `${video.framesPerSecond} fps`,
    'Bit rate': formatBitRate(video.bitsPerSecond),
    'Frames shown': formatCount(video.framesDecoded),
    'Frames dropped': formatCount(video.framesDropped),
    'Key frames': formatCount(video.keyFramesDecoded),
    'Packets lost': formatCount(video.packetsLost),
    'Jitter': video.jitterMs === undefined ? EMPTY : `${video.jitterMs} ms`,
  });
}

function showTileInfo(perSource) {
  for (const [id, tile] of tiles) {
    const stats = perSource[id];
    tile.setInfo(stats?.width
      ? `${stats.width} × ${stats.height} · ${stats.framesPerSecond} fps · ${formatBitRate(stats.bitsPerSecond)}`
      : '');
  }
}

async function pollStats(s) {
  if (session !== s) {
    return;
  }

  const state = s.state;
  const mode = state?.video.mode;
  const streaming = state?.streaming.state;
  const snapshot = {
    state: 'connected',
    mode: mode ?? null,
    streaming: streaming ?? null,
    recording: state?.recording.state ?? null,
    layout: state?.layout ?? null,
    tiles: [...tiles.keys()],
    popouts: [...popouts.keys()],
    zoom: Object.fromEntries([...tiles].map(([id, tile]) => [id, Math.round(tile.zoom.scale * 100) / 100])),
    sources: {},
    video: null,
    audio: null,
  };

  if (streaming && streaming !== 'stopped' && mode === 'jpeg') {
    pollPictureStats(s, snapshot);
  } else if (s.peer) {
    await pollPeerStats(s, snapshot);
  } else {
    showStats('video', null);
    showStats('audio', null);
    showTileInfo({});
    el.level.value = 0;
    showStats('connection', {
      'Transport': s.socketOpened ? 'WebSocket (control only)' : EMPTY,
      'Received': formatBytes(s.bytesReceived),
      'Connected for': s.openedAt ? formatDuration(performance.now() - s.openedAt) : EMPTY,
    });
  }

  if (session === s) {
    s.snapshot = snapshot;
  }
}

function pollPictureStats(s, snapshot) {
  const now = performance.now();
  for (const [id, counters] of s.pictures) {
    const frameRate = rateOf(s, `pictures:${id}`, counters.shown, now);
    const bits = rateOf(s, `pictureBits:${id}`, counters.bytes * 8, now);
    snapshot.sources[id] = {
      width: counters.width,
      height: counters.height,
      framesPerSecond: Math.round((frameRate ?? 0) * 10) / 10,
      bitsPerSecond: Math.round(bits ?? 0),
      codec: 'JPEG',
      framesDecoded: counters.shown,
      framesDropped: counters.skipped,
      framesReceived: counters.received,
    };
  }
  snapshot.video = summarizeVideo(snapshot.sources);
  showVideoSummary(snapshot.video);
  showTileInfo(snapshot.sources);

  const audioBits = rateOf(s, 'audioBits', s.audio.bytes * 8, now);
  showStats('audio', s.audio.blocks === 0 ? null : {
    'Codec': 'PCM 16-bit, 8 kHz',
    'Bit rate': formatBitRate(audioBits),
    'Packets received': formatCount(s.audio.blocks),
    'Buffered': s.pcm ? formatMilliseconds(s.pcm.buffered) : EMPTY,
  });
  el.level.value = s.audio.level;
  s.audio.level *= 0.5;

  showStats('connection', {
    'Transport': 'WebSocket',
    'Route': s.url,
    'Received': formatBytes(s.bytesReceived),
    'Connected for': formatDuration(now - s.openedAt),
  });

  if (s.audio.blocks > 0) {
    snapshot.audio = {
      codec: 'PCM 16-bit, 8 kHz',
      bitsPerSecond: Math.round(audioBits ?? 0),
      packetsReceived: s.audio.blocks,
      totalSamplesReceived: s.audio.samples,
      bufferedSeconds: s.pcm ? Math.round(s.pcm.buffered * 1000) / 1000 : 0,
      contextState: s.pcm?.context.state ?? null,
    };
  }
}

async function pollPeerStats(s, snapshot) {
  const peer = s.peer;
  let report;
  try {
    report = await peer.getStats();
  } catch {
    return;
  }
  if (session !== s || s.peer !== peer) {
    return;
  }

  const stats = new Map();
  const videos = [];
  let audio;
  let pair;
  report.forEach((stat) => {
    stats.set(stat.id, stat);
    if (stat.type === 'inbound-rtp' && stat.kind === 'video') {
      videos.push(stat);
    } else if (stat.type === 'inbound-rtp' && stat.kind === 'audio') {
      audio = stat;
    }
  });
  report.forEach((stat) => {
    if (stat.type === 'transport' && stat.selectedCandidatePairId) {
      pair = stats.get(stat.selectedCandidatePairId);
    } else if (!pair && stat.type === 'candidate-pair' && stat.selected) {
      pair = stat; // Firefox marks the pair directly
    }
  });

  for (const video of videos) {
    const id = s.trackToSource.get(video.trackIdentifier) ?? [...s.tracks.keys()].find((key) => s.tracks.get(key)?.id === video.trackIdentifier);
    if (!id) {
      continue;
    }
    const bitRate = rateOf(s, `video:${id}`, video.bytesReceived * 8, video.timestamp);
    snapshot.sources[id] = {
      width: video.frameWidth ?? 0,
      height: video.frameHeight ?? 0,
      framesPerSecond: video.framesPerSecond ?? 0,
      bitsPerSecond: Math.round(bitRate ?? 0),
      codec: describeCodec(stats.get(video.codecId)),
      framesDecoded: video.framesDecoded ?? 0,
      framesDropped: video.framesDropped ?? 0,
      keyFramesDecoded: video.keyFramesDecoded ?? 0,
      packetsReceived: video.packetsReceived ?? 0,
      packetsLost: video.packetsLost ?? 0,
      jitterMs: Math.round((video.jitter ?? 0) * 1000),
    };
  }
  snapshot.video = summarizeVideo(snapshot.sources);
  showVideoSummary(snapshot.video);
  showTileInfo(snapshot.sources);

  if (audio && !POPOUT) {
    const bitRate = rateOf(s, 'audio', audio.bytesReceived * 8, audio.timestamp);
    const codec = describeCodec(stats.get(audio.codecId));
    showStats('audio', {
      'Codec': codec,
      'Bit rate': formatBitRate(bitRate),
      'Packets received': formatCount(audio.packetsReceived),
      'Packets lost': formatCount(audio.packetsLost),
      'Buffered': audio.jitterBufferEmittedCount ? formatMilliseconds(audio.jitterBufferDelay / audio.jitterBufferEmittedCount) : EMPTY,
    });
    el.level.value = audio.audioLevel ?? 0;
    snapshot.audio = {
      codec,
      bitsPerSecond: Math.round(bitRate ?? 0),
      packetsReceived: audio.packetsReceived ?? 0,
      packetsLost: audio.packetsLost ?? 0,
      totalSamplesReceived: audio.totalSamplesReceived ?? 0,
      jitterMs: Math.round((audio.jitter ?? 0) * 1000),
      audioLevel: audio.audioLevel ?? 0,
    };
  } else {
    showStats('audio', null);
  }

  showStats('connection', {
    'Transport': `WebRTC (${peer.connectionState})`,
    'Route': pair ? `${describeCandidate(stats.get(pair.localCandidateId))} → ${describeCandidate(stats.get(pair.remoteCandidateId))}` : EMPTY,
    'Round trip': formatMilliseconds(pair?.currentRoundTripTime),
    'Received': formatBytes(pair?.bytesReceived),
    'Connected for': s.peerConnectedAt ? formatDuration(performance.now() - s.peerConnectedAt) : EMPTY,
  });

  snapshot.state = peer.connectionState;
}

// ---------------------------------------------------------------------------------------------
// Wiring
// ---------------------------------------------------------------------------------------------

el.form.addEventListener('submit', (event) => {
  event.preventDefault();
  if (session) {
    log('Disconnected');
    disconnect();
    setStatus('idle', 'Not connected');
  } else {
    connect();
  }
});

for (const target of ['recording', 'streaming']) {
  const row = el.control[target];
  row.start.addEventListener('click', () => sendCommand(target, 'start'));
  row.stop.addEventListener('click', () => sendCommand(target, 'stop'));
  row.pause.addEventListener('click', () => sendCommand(target, session?.state?.[target].state === 'paused' ? 'resume' : 'pause'));
}

el.layout.addEventListener('change', () => configure('layout', el.layout.value));
el.videoMode.addEventListener('change', () => configure('videoMode', el.videoMode.value));
el.frameRate.addEventListener('change', () => {
  const value = Number(el.frameRate.value);
  if (Number.isInteger(value) && value >= 1 && value <= 60) {
    configure('frameRate', value);
  } else {
    showControlNote('The frame rate has to be a whole number from 1 to 60.', 'error');
  }
});

function setMuted(value) {
  muted = value;
  el.audio.muted = value;
  session?.pcm?.setMuted(value);
  if (!value) {
    session?.pcm?.resume();
    if (el.audio.srcObject) {
      el.audio.play().catch(() => {});
    }
  }
  el.unmute.hidden = true;
  updatePlaybackControls();
}

el.mute.addEventListener('click', () => setMuted(!muted));
el.unmute.addEventListener('click', () => setMuted(false));
el.fullscreen.addEventListener('click', () => toggleFullscreen(el.stage));
document.addEventListener('fullscreenchange', updatePlaybackControls);
el.clearLog.addEventListener('click', () => el.log.replaceChildren());
window.addEventListener('pagehide', () => {
  disconnect();
  closePopouts();
});

// For the browser console and automated checks: the latest statistics poll and capture state.
window.captureViewer = {
  get stats() {
    return session?.snapshot ?? null;
  },
  get state() {
    return session?.state ?? null;
  },
};

if (POPOUT) {
  document.body.classList.add('popout');
  document.title = `${POPOUT} – Capture Viewer`;
}

buildStatPanels();
showControls(null);
renderTiles();

// ?url=ws://host:port/ presets the address; ?autoconnect connects on load.
let initialUrl = parameters.get('url');
if (!initialUrl && !POPOUT) {
  try {
    initialUrl = localStorage.getItem(URL_STORAGE_KEY);
  } catch {
    initialUrl = null;
  }
}
el.url.value = initialUrl || DEFAULT_URL;

if (parameters.has('autoconnect')) {
  connect();
}
