// Capture Viewer
//
// A test client for the capture client. It connects to the capture client's WebSocket, shows what
// the capture client is doing, plays its video streams (one per screen, or one for the whole
// desktop) and lets you control it. Each screen can be zoomed with the mouse wheel and moved into
// a window of its own: a popped-out screen is this same page in a window of its own
// (?popout=<stream id>), with its own connection that asks for that one stream and no audio.
//
// The messages are in protocol.js. The page keeps one Session per connection, and everything on
// the page is drawn from that session's state by render(). What differs between the main window
// and a pop-out is in one place: the window's role (mainRole, popoutRole).

import { describeCodec, EMPTY, formatBitRate, formatBytes, formatCount, formatDuration, formatFrameRate, formatMilliseconds } from './format.js';
import { PcmPlayer } from './pcm-player.js';
import {
  addressFor, AUDIO_MESSAGE, commandMessage, configureMessage, isDefaultDeviceEntry, parseMediaMessage, pcmSamples, PICTURE_MESSAGE, selectMessage,
} from './protocol.js';
import { Zoom } from './zoom.js';

const DEFAULT_URL = 'ws://localhost:8081/';
const URL_STORAGE_KEY = 'capture-viewer.url';
const STATS_INTERVAL_MS = 1000;
const MAX_LOG_LINES = 300;

const STAT_FIELDS = {
  video: ['Streams', 'Codec', 'Frame rate', 'Bit rate', 'Frames shown', 'Frames dropped', 'Key frames', 'Packets lost', 'Jitter'],
  audio: ['Codec', 'Bit rate', 'Packets received', 'Packets lost', 'Buffered'],
  connection: ['Transport', 'Route', 'Round trip', 'Received', 'Connected for'],
};

const parameters = new URLSearchParams(location.search);

/** What this window is for: see mainRole and popoutRole. A ?popout= that names no stream is the main window. */
const role = parameters.get('popout') ? popoutRole(parameters.get('popout')) : mainRole();

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
  webrtcAudio: byId('webrtc-audio'),
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

/** The id of the last command or configure request; an error refusing a request carries its id. */
let lastRequestId = 0;

// ---------------------------------------------------------------------------------------------
// The window's role: the main window, or a pop-out showing one stream
// ---------------------------------------------------------------------------------------------

/**
 * The main window shows the streams the person ticks (less those popped out), plays the sound,
 * holds the controls and remembers the address.
 */
function mainRole() {
  return {
    hasControls: true,
    playsAudio: true,
    socketAddress: (url) => url,
    /** The streams to show, in the order the screens are arranged. */
    wantedSources: (sources) => sources
      .filter((source) => !deselected.has(source.id) && !popouts.has(source.id))
      .sort((a, b) => a.left - b.left || a.top - b.top),
    /** Title and text for the stage while streaming is on but no stream is shown. */
    describeNothingShown(state) {
      if (state.sources.length === 0) {
        return ['No screens to show', 'The capture client offers no screens right now.'];
      }
      return state.sources.every((source) => popouts.has(source.id))
        ? ['Every screen is in its own window', 'Close one of those windows, or press Bring back under Watch.']
        : ['No screen selected', 'Tick a screen under Watch.'];
    },
    /** Sets up a tile's window button. */
    setUpWindowButton(button, id) {
      button.title = 'Show this screen in a window of its own';
      button.addEventListener('click', () => popOut(id));
    },
    titleFor: () => null, // the page's own
    rememberAddress(url) {
      try {
        localStorage.setItem(URL_STORAGE_KEY, url);
      } catch {
        // Storage can be unavailable (private window, file:// page); the address just is not remembered.
      }
    },
    savedAddress() {
      try {
        return localStorage.getItem(URL_STORAGE_KEY);
      } catch {
        return null;
      }
    },
  };
}

/**
 * A pop-out (?popout=<stream id>) is this same page in a window of its own, with its own
 * connection: it shows its one stream, silently, and nothing else.
 */
function popoutRole(id) {
  return {
    hasControls: false,
    playsAudio: false,
    // It asks for its one stream and no audio as it connects, so it is never sent anything else.
    socketAddress: (url) => addressFor(url, id),
    wantedSources: (sources) => sources.filter((source) => source.id === id),
    describeNothingShown: () => [`${sourceName(id)} is no longer captured`, 'The capture client changed its layout. Close this window.'],
    setUpWindowButton(button) {
      button.textContent = 'Close';
      button.title = 'Close this window. The screen goes back to the main window.';
      button.addEventListener('click', () => window.close());
    },
    titleFor: (state) => `${state?.sources.find((source) => source.id === id)?.name ?? id} – Capture Viewer`,
    rememberAddress() {}, // the main window's address is the one to remember
    savedAddress: () => null,
  };
}

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

function sourceById(id) {
  return session?.state?.sources.find((source) => source.id === id) ?? null;
}

function sourceName(id) {
  return sourceById(id)?.name ?? id;
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
    this.waitingSince = performance.now();

    this.zoom = new Zoom(this.view, [this.video, this.canvas], (scale) => {
      this.zoomButton.textContent = `${Math.round(scale * 100)}%`;
    });
    this.zoomButton.addEventListener('click', () => this.zoom.reset());
    this.fullscreenButton.addEventListener('click', () => toggleFullscreen(this.element));

    role.setUpWindowButton(this.popoutButton, this.id);

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
      log(`${this.source.name}: first picture after ${Math.round(performance.now() - this.waitingSince)} ms`, 'good');
    }
  }

  setReceiving(receiving) {
    if (!receiving) {
      this.waitingSince = performance.now(); // for the "first picture after" log
    }
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

/** Brings the tiles in line with the streams this window should show. */
function renderTiles() {
  const s = session;
  const state = s?.state;
  const wanted = state && state.streaming.state !== 'stopped' ? role.wantedSources(state.sources) : [];
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
    tile.attach(jpeg ? null : s.rtc.tracks.get(source.id) ?? null);
  }

  // Re-append only when the order is wrong: moving a playing video element can interrupt it.
  const order = wanted.map((source) => tiles.get(source.id).element);
  if (order.some((element, index) => el.tiles.children[index] !== element)) {
    el.tiles.append(...order);
  }
  el.tiles.style.setProperty('--columns', wanted.length <= 1 ? 1 : wanted.length <= 4 ? 2 : 3);
}

/**
 * Draws the whole page from the session's state and this window's own choices (screens ticked or
 * popped out, sound muted). Whatever changes either calls this; each part only changes what is
 * different, so calling it often is cheap. Telling the capture client what this window wants is
 * separate: syncSelection().
 */
function render() {
  const state = session?.state ?? null;
  renderTiles();
  updatePlaceholder();
  updatePlaybackControls();
  renderScreenList();
  showControls(state);
  renderAudioDevices(state);
  el.pausedBadge.hidden = state?.streaming.state !== 'paused';
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
    [title, text] = role.describeNothingShown(state);
  }

  el.placeholder.hidden = s !== null && state !== null && tiles.size > 0;
  el.placeholderTitle.textContent = title;
  el.placeholderText.textContent = text;
}

/** Tells the capture client which streams this window shows, so it is sent (and has to encode) nothing else. */
function syncSelection() {
  const s = session;
  if (!s?.state || s.socket.readyState !== WebSocket.OPEN) {
    return;
  }
  const message = selectMessage([...tiles.keys()].sort(), role.playsAudio);
  const key = JSON.stringify(message);
  if (key !== s.lastSelection) {
    s.lastSelection = key;
    send(message);
  }
}

/** The list under Watch: a tick box and a pop-out button per stream. */
function renderScreenList() {
  if (!role.hasControls) {
    return;
  }

  const sources = session?.state?.sources ?? [];
  // Everything the list shows, so that it is rebuilt exactly when it would look different.
  const key = JSON.stringify([session !== null, sources.map((s) => [s.id, s.name, s.width, s.height, s.primary, s.screens]), [...deselected], [...popouts.keys()]]);
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
      render();
      syncSelection();
    });
    const name = document.createElement('span');
    name.className = 'screen-name';
    name.textContent = source.name;
    label.append(box, name);

    const button = document.createElement('button');
    button.type = 'button';
    button.dataset.source = source.id;
    button.textContent = poppedOut ? 'Bring back' : 'Pop out';
    button.setAttribute('aria-label', `${button.textContent}: ${source.name}`);
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
  render();
  syncSelection();
}

function closeWindow(win) {
  try {
    win?.close();
  } catch {
    // Already gone.
  }
}

/** Ends a pop-out window: its screen is shown in this window again. */
function bringBack(id) {
  closeWindow(popouts.get(id));
  screensBack([id]);
}

/** Screens whose pop-out windows have gone: they are shown here again. */
function screensBack(ids) {
  for (const id of ids) {
    popouts.delete(id);
    log(`${sourceName(id)} is back in this window`);
  }
  render();
  syncSelection();
}

function closePopouts() {
  popouts.forEach(closeWindow);
  popouts.clear();
}

// A pop-out that is closed (by its own button, the window's close box or anything else) returns its screen here.
setInterval(() => {
  const closed = [...popouts].filter(([, win]) => win.closed).map(([id]) => id);
  if (closed.length > 0) {
    screensBack(closed);
  }
}, 500);

// ---------------------------------------------------------------------------------------------
// Connection
// ---------------------------------------------------------------------------------------------

/**
 * One connection to the capture client, and everything learned through it. Every connection gets
 * a new session, and the handlers of a connection check that its session is still the current one
 * (`session === s`), so that nothing from an old connection reaches the page.
 */
class Session {
  constructor(url) {
    this.url = url;

    // The WebSocket: control messages both ways, and media in JPEG mode.
    this.socket = null;
    this.socketOpened = false;
    this.openedAt = 0;
    this.bytesReceived = 0;
    this.signaling = Promise.resolve(); // text messages, handled strictly one after another

    // What the capture client says it is doing, and how this window's requests to it went.
    this.state = null;
    this.lastSelection = '';  // the last select message sent, as JSON
    this.audioPending = null; // {ids, request, until}: an audio device change sent but not yet confirmed
    this.audioError = null;   // why the last audio device change was refused
    this.controlError = null; // why the last other command or setting was refused

    /** H.264 mode: the WebRTC connection; see acceptOffer and closePeer. */
    this.rtc = Session.noRtc();

    /** JPEG mode: pictures and PCM audio over the WebSocket; see handleMedia and closeSocketMedia. */
    this.media = Session.noMedia();

    // Statistics: polled every second (pollStats).
    this.statsTimer = 0;
    this.polling = false; // a poll is waiting for the browser's statistics; the next one is skipped
    this.rateMemory = {}; // the counters at the last poll, for rates and averages since then
    this.snapshot = { state: 'connecting' };
  }

  static noRtc() {
    return {
      peer: null,
      tracks: new Map(),        // stream id -> video track
      trackToSource: new Map(), // track id -> stream id
      connectedAt: 0,
      failure: null,            // why the last offer could not be answered
    };
  }

  static noMedia() {
    return {
      pictures: new Map(), // stream id -> picture counters (Session.pictureCounters)
      pcm: null,           // the PcmPlayer, once audio arrives
      pcmCounters: { blocks: 0, samples: 0, bytes: 0, level: 0 },
    };
  }

  static pictureCounters() {
    return { decoding: false, pending: null, received: 0, shown: 0, skipped: 0, bytes: 0, width: 0, height: 0 };
  }
}

function connect() {
  const url = el.url.value.trim();
  if (!/^wss?:\/\/\S+$/i.test(url)) {
    setStatus('error', 'The address has to start with ws:// or wss://');
    el.url.focus();
    return;
  }

  disconnect();

  const s = new Session(url);
  try {
    s.socket = new WebSocket(role.socketAddress(url));
  } catch (error) {
    setStatus('error', `That address cannot be used: ${error.message}`);
    return;
  }
  role.rememberAddress(url); // only an address the browser accepts

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

/** Sends a command or configure request with a new id, and returns the id. */
function sendRequest(message) {
  const id = ++lastRequestId;
  send({ ...message, id });
  return id;
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
      if (s.audioPending && message.request === s.audioPending.request) {
        // The audio change just asked for was refused: the list goes back to what is recorded.
        s.audioPending = null;
        s.audioError = message.message;
      } else {
        s.controlError = message.message;
      }
      render();
    } else if (message.sdp) {
      if (message.type === 'offer') {
        await acceptOffer(s, message);
      }
    } else if (message.candidate) {
      // The capture client sends a connection's candidates after its offer, and messages are
      // handled in order, so a candidate with no offer set belongs to a connection already closed.
      if (s.rtc.peer?.remoteDescription) {
        await s.rtc.peer.addIceCandidate(message);
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
  closeSocketMedia(s);
  closePopouts();

  el.unmute.hidden = true;
  clearStats();
  render();
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
    const live = [...s.media.pictures.values()].some((counters) => counters.shown > 0);
    setStatus(live ? 'live' : 'connecting', live ? 'Live · JPEG' : 'Waiting for pictures…');
  } else if (s.rtc.failure) {
    setStatus('error', `The H.264 stream could not start: ${s.rtc.failure}`);
  } else {
    const connected = s.rtc.peer?.connectionState === 'connected';
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
  const sourcesChanged = !previous || previous.sources.map((source) => source.id).join(',') !== state.sources.map((source) => source.id).join(',');

  if (!previous || previous.streaming.state !== streaming) {
    log(`Streaming is ${streaming}`);
  }
  if (!previous || previous.video.mode !== mode) {
    log(`Video method: ${mode === 'jpeg' ? 'JPEG pictures over the WebSocket' : 'H.264 over WebRTC'}`);
  }
  if (sourcesChanged) {
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
        closeWindow(win);
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


  // Each transport only lives while the capture client is using it.
  if (streaming === 'stopped' || mode !== 'h264') {
    closePeer(s);
  }
  if (streaming === 'stopped' || mode !== 'jpeg') {
    closeSocketMedia(s);
  }

  document.title = role.titleFor(state) ?? document.title;

  render();
  syncSelection();
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

  // Why the panel cannot be used, or why the last request was refused.
  const error = enabled ? session?.controlError ?? null : null;
  showControlNote(!state ? 'Connect to control the capture client.'
    : !state.remoteControl ? 'Remote control is turned off on the capture client.'
    : error ?? '', error ? 'error' : 'info');
}

// ---------------------------------------------------------------------------------------------
// Audio devices: a tick box for each device the capture client can record
// ---------------------------------------------------------------------------------------------

function sameMembers(a = [], b = []) {
  return a.length === b.length && a.every((item) => b.includes(item));
}

/** What is selected as far as this page knows: a change it asked for counts before the capture client confirms it. */
function audioSelection(s) {
  return s?.audioPending?.ids ?? s?.state?.audio?.selected ?? [];
}

/** "the default recording device (Microphone (USB Audio))", or the device's name. */
function audioDeviceLabel(device) {
  if (isDefaultDeviceEntry(device)) {
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
  if (isDefaultDeviceEntry(device)) {
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
  name.className = isDefaultDeviceEntry(device) ? 'device-name stand-in' : 'device-name';
  name.textContent = isDefaultDeviceEntry(device) ? 'Default device' : device.name;
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
  log(`Request: ${record ? 'record' : 'stop recording'} ${audioDeviceLabel(device)}`);
  const request = sendRequest(configureMessage({ audioDevices: ids }));
  s.audioPending = { ids, request, until: performance.now() + 3000 };
  s.audioError = null;
  render();
}

/** A new request: the refusal of the last one no longer applies. */
function clearControlError() {
  if (session?.controlError) {
    session.controlError = null;
    render();
  }
}

function sendCommand(target, action) {
  clearControlError();
  log(`Request: ${action} ${target}`);
  sendRequest(commandMessage(target, action));
}

function configure(setting, value) {
  clearControlError();
  log(`Request: ${setting} = ${value}`);
  sendRequest(configureMessage({ [setting]: value }));
}

// ---------------------------------------------------------------------------------------------
// H.264 mode: WebRTC
// ---------------------------------------------------------------------------------------------

async function acceptOffer(s, offer) {
  closePeer(s);

  const peer = new RTCPeerConnection();
  s.rtc.peer = peer;
  s.rateMemory = {};
  const sourceByMid = new Map(Object.entries(offer.sources ?? {}));
  log(`Offer received (${sourceByMid.size} video stream${sourceByMid.size === 1 ? '' : 's'}${/^m=audio/m.test(offer.sdp) ? ' and audio' : ''})`);

  peer.onicecandidate = (event) => {
    if (event.candidate && s.rtc.peer === peer) {
      send(event.candidate);
    }
  };

  peer.onconnectionstatechange = () => {
    if (s.rtc.peer !== peer) {
      return;
    }
    const state = peer.connectionState;
    log(`WebRTC ${state}`, state === 'connected' ? 'good' : state === 'failed' ? 'error' : 'info');
    if (state === 'connected') {
      s.rtc.connectedAt = performance.now();
    }
    refreshStatus(s);
  };

  try {
    await peer.setRemoteDescription({ type: 'offer', sdp: offer.sdp });

    // Each transceiver's mid says which stream its track carries; the tracks exist from here on and
    // start delivering pictures once the connection is up.
    for (const transceiver of peer.getTransceivers()) {
      const track = transceiver.receiver.track;
      if (track.kind === 'audio') {
        if (role.playsAudio) {
          playAudio(track);
        }
      } else if (sourceByMid.has(transceiver.mid)) {
        const id = sourceByMid.get(transceiver.mid);
        s.rtc.tracks.set(id, track);
        s.rtc.trackToSource.set(track.id, id);
      }
      // A live view: ask for the smallest receive buffer the network allows.
      if ('jitterBufferTarget' in transceiver.receiver) {
        transceiver.receiver.jitterBufferTarget = 0;
      }
    }
    render(); // the tiles get their tracks

    await peer.setLocalDescription(await peer.createAnswer());
  } catch (error) {
    // A connection that cannot answer its offer is no use: close it, and say why there is no
    // picture until the capture client makes a new offer.
    if (s.rtc.peer === peer) {
      closePeer(s);
      s.rtc.failure = error.message;
      render();
      refreshStatus(s);
    }
    throw error; // handleMessage logs it
  }

  if (s.rtc.peer === peer) {
    send(peer.localDescription);
  }
}

/** Closes the WebRTC connection, if there is one, and forgets everything about it. */
function closePeer(s) {
  const peer = s.rtc.peer;
  s.rtc = Session.noRtc();
  if (!peer) {
    return;
  }
  peer.close();
  el.webrtcAudio.srcObject = null;
  el.unmute.hidden = true; // the sound it was for has gone
  for (const tile of tiles.values()) {
    tile.attach(null);
  }
}

async function playAudio(track) {
  el.webrtcAudio.srcObject = new MediaStream([track]);
  el.webrtcAudio.muted = muted;
  try {
    await el.webrtcAudio.play();
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

/** Whether pictures and PCM audio come over the WebSocket: in JPEG mode, unless streaming is stopped. */
function socketMediaFlows(s) {
  return s.state !== null && s.state.streaming.state !== 'stopped' && s.state.video.mode === 'jpeg';
}

function handleMedia(s, buffer) {
  const message = parseMediaMessage(buffer);
  if (!message || !socketMediaFlows(s)) {
    return; // late media, sent before streaming stopped or the mode changed
  }

  if (message.type === PICTURE_MESSAGE) {
    const source = s.state.sources.find((candidate) => candidate.number === message.number);
    if (!source) {
      return; // a stream from before a layout change
    }
    let counters = s.media.pictures.get(source.id);
    if (!counters) {
      counters = Session.pictureCounters();
      s.media.pictures.set(source.id, counters);
    }
    counters.received++;
    counters.bytes += message.payload.byteLength;
    showPicture(s, source.id, counters, message.payload);
  } else if (message.type === AUDIO_MESSAGE && role.playsAudio) {
    playPcm(s, pcmSamples(message.payload));
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
      if (session !== s || !socketMediaFlows(s) || !tile) {
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
    if (counters.pending) {
      // The loop ended early (a picture failed, or the tile went away). The waiting picture is
      // dropped: shown later, it would be drawn after a newer one.
      counters.skipped++;
      counters.pending = null;
    }
  }
}

function playPcm(s, samples) {
  if (!s.media.pcm) {
    s.media.pcm = new PcmPlayer(s.state.audio.sampleRate, muted);
    el.unmute.hidden = !s.media.pcm.suspended;
  }

  s.media.pcmCounters.blocks++;
  s.media.pcmCounters.samples += samples.length;
  s.media.pcmCounters.bytes += samples.byteLength;

  let peak = 0;
  for (let i = 0; i < samples.length; i++) {
    peak = Math.max(peak, Math.abs(samples[i]));
  }
  s.media.pcmCounters.level = Math.max(peak / 32768, s.media.pcmCounters.level * 0.8);

  s.media.pcm.play(samples);
}

function closeSocketMedia(s) {
  if (!s.media.pcm && s.media.pictures.size === 0 && s.media.pcmCounters.blocks === 0) {
    return; // nothing was open
  }

  s.media.pcm?.close();
  s.media = Session.noMedia();
  el.unmute.hidden = true; // the sound it was for has gone
  s.rateMemory = {};
  for (const tile of tiles.values()) {
    tile.clearPicture();
  }
}

// ---------------------------------------------------------------------------------------------
// Statistics
// ---------------------------------------------------------------------------------------------

/** Per-second rate of a counter, from the difference to the previous poll. */
function rateOf(s, key, value, timestamp) {
  const previous = s.rateMemory[key];
  s.rateMemory[key] = { value, timestamp };
  if (value === undefined || !previous || timestamp <= previous.timestamp) {
    return undefined;
  }
  return ((value - previous.value) * 1000) / (timestamp - previous.timestamp);
}

/**
 * The average of something since the previous poll: the change in its total over the change in its
 * count. Undefined at the first poll, or when nothing was counted in between.
 */
function averageSinceLastPoll(s, key, total, count) {
  const previous = s.rateMemory[key];
  s.rateMemory[key] = { total, count };
  if (total === undefined || count === undefined || !previous || count <= previous.count) {
    return undefined;
  }
  return (total - previous.total) / (count - previous.count);
}

/** Rounded to the given number of decimal places; an unknown value stays unknown. */
function roundTo(value, places) {
  if (value === undefined) {
    return undefined;
  }
  const scale = 10 ** places;
  return Math.round(value * scale) / scale;
}

/** Seconds as whole milliseconds; an unknown value stays unknown. */
function wholeMilliseconds(seconds) {
  return seconds === undefined ? undefined : Math.round(seconds * 1000);
}

function describeCandidate(candidate) {
  if (!candidate) {
    return '?';
  }
  const address = candidate.address ?? candidate.ip;
  return address ? `${candidate.candidateType} ${address}:${candidate.port}` : candidate.candidateType;
}

/** Totals over the streams shown in this window, for the Video panel; over the streams that know each number. */
function summarizeVideo(perSource) {
  const shown = [...tiles.keys()].map((id) => perSource[id]).filter(Boolean);
  if (shown.length === 0) {
    return null;
  }
  const known = (key) => shown.map((stats) => stats[key]).filter((value) => value !== undefined);
  const total = (values) => values.reduce((sum, value) => sum + value, 0);
  const sum = (key) => {
    const values = known(key);
    return values.length === 0 ? undefined : total(values);
  };
  const frameRates = known('framesPerSecond');
  const jitters = known('jitterMs');
  return {
    streams: shown.length,
    codec: shown[0].codec,
    framesPerSecond: frameRates.length === 0 ? undefined : roundTo(total(frameRates) / frameRates.length, 1),
    bitsPerSecond: sum('bitsPerSecond'),
    framesDecoded: sum('framesDecoded'),
    framesDropped: sum('framesDropped'),
    keyFramesDecoded: sum('keyFramesDecoded'),
    packetsLost: sum('packetsLost'),
    jitterMs: jitters.length === 0 ? undefined : Math.max(...jitters),
  };
}

function showVideoSummary(video) {
  showStats('video', video && {
    'Streams': String(video.streams),
    'Codec': video.codec,
    'Frame rate': formatFrameRate(video.framesPerSecond),
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
      ? `${stats.width} × ${stats.height} · ${formatFrameRate(stats.framesPerSecond)} · ${formatBitRate(stats.bitsPerSecond)}`
      : '');
  }
}

/**
 * What one statistics poll found, whichever way the media came: each source of statistics below
 * (measureControlOnly, measureSocketMedia, measurePeer) fills one of these, and pollStats shows it.
 * A number not known yet is left out (a rate needs two polls), and its panel shows a dash.
 * @typedef {object} Measurements
 * @property {Object<string, StreamStats>} sources   by stream id
 * @property {AudioStats|null} audio
 * @property {number} level              the audio level, 0 to 1
 * @property {ConnectionStats} connection
 * @property {string} [state]            the WebRTC connection's state, when there is one
 *
 * @typedef {object} StreamStats
 * @property {number} [width]
 * @property {number} [height]
 * @property {number} [framesPerSecond]
 * @property {number} [bitsPerSecond]
 * @property {string} codec              'JPEG', or the WebRTC codec ('H.264 Constrained Baseline')
 * @property {number} [framesDecoded]    pictures shown
 * @property {number} [framesDropped]
 *
 * @typedef {object} AudioStats
 * @property {string} codec
 * @property {number} [bitsPerSecond]
 * @property {number} [packetsReceived]
 * @property {number} [packetsLost]      WebRTC only
 * @property {number} [bufferedSeconds]  how long sound waits before it is played, now: in JPEG mode the
 *                                       PCM queued, in WebRTC the jitter buffer's delay since the last poll
 *
 * @typedef {object} ConnectionStats
 * @property {string} transport
 * @property {string} [route]
 * @property {number} [roundTripSeconds]
 * @property {number} [bytesReceived]
 * @property {number} [connectedAt]      performance.now() when the connection came up
 */

/**
 * What the last statistics poll found. The panels show it, and the end-to-end test
 * (tests/EndToEnd.ps1) reads it through window.captureViewer.stats, so these names are a contract.
 * @typedef {object} StatsSnapshot
 * @property {string} state              'connecting', 'connected', or the WebRTC connection's state
 * @property {string|null} mode          the video mode
 * @property {string|null} streaming     the streaming state
 * @property {string|null} recording     the recording state
 * @property {string|null} layout
 * @property {string[]} tiles            the streams shown in this window
 * @property {string[]} popouts          the streams in windows of their own
 * @property {Object<string, number>} zoom   each tile's zoom, by stream id
 * @property {Object<string, StreamStats>} sources   by stream id
 * @property {object|null} video         totals over this window's tiles: streams, codec, framesPerSecond, bitsPerSecond, framesDecoded, ...
 * @property {AudioStats|null} audio     null in a pop-out
 */

async function pollStats(s) {
  if (session !== s || s.polling) {
    return; // a newer connection, or the last poll is still waiting for the browser's statistics
  }

  s.polling = true;
  try {
    const found = socketMediaFlows(s) ? measureSocketMedia(s) : s.rtc.peer ? await measurePeer(s) : measureControlOnly(s);
    if (session !== s || !found) {
      return; // a newer connection, or the WebRTC connection went away while it was asked
    }

    const video = summarizeVideo(found.sources);
    showVideoSummary(video);
    showTileInfo(found.sources);
    showAudioStats(found.audio);
    el.level.value = found.level;
    showConnectionStats(found.connection);

    // Read after the wait for the statistics, so that the snapshot describes one moment.
    const state = s.state;

    /** @type {StatsSnapshot} */
    s.snapshot = {
      state: found.state ?? (s.socketOpened ? 'connected' : 'connecting'),
      mode: state?.video.mode ?? null,
      streaming: state?.streaming.state ?? null,
      recording: state?.recording.state ?? null,
      layout: state?.layout ?? null,
      tiles: [...tiles.keys()],
      popouts: [...popouts.keys()],
      zoom: Object.fromEntries([...tiles].map(([id, tile]) => [id, Math.round(tile.zoom.scale * 100) / 100])),
      sources: found.sources,
      video,
      audio: found.audio,
    };
  } finally {
    s.polling = false;
  }
}

function showAudioStats(audio) {
  showStats('audio', audio && {
    'Codec': audio.codec,
    'Bit rate': formatBitRate(audio.bitsPerSecond),
    'Packets received': formatCount(audio.packetsReceived),
    'Packets lost': formatCount(audio.packetsLost),
    'Buffered': formatMilliseconds(audio.bufferedSeconds),
  });
}

function showConnectionStats(connection) {
  showStats('connection', {
    'Transport': connection.transport,
    'Route': connection.route ?? EMPTY,
    'Round trip': formatMilliseconds(connection.roundTripSeconds),
    'Received': formatBytes(connection.bytesReceived),
    'Connected for': connection.connectedAt ? formatDuration(performance.now() - connection.connectedAt) : EMPTY,
  });
}

/** Nothing is streaming to this window: there is only the WebSocket, for control. */
function measureControlOnly(s) {
  return {
    sources: {},
    audio: null,
    level: 0,
    connection: { transport: s.socketOpened ? 'WebSocket (control only)' : EMPTY, bytesReceived: s.bytesReceived, connectedAt: s.openedAt },
  };
}

/** JPEG mode: the pictures and PCM audio counted as they arrived over the WebSocket. */
function measureSocketMedia(s) {
  const now = performance.now();
  const sources = {};
  for (const [id, counters] of s.media.pictures) {
    sources[id] = {
      width: counters.width,
      height: counters.height,
      framesPerSecond: roundTo(rateOf(s, `pictures:${id}`, counters.shown, now), 1),
      bitsPerSecond: roundTo(rateOf(s, `pictureBits:${id}`, counters.bytes * 8, now), 0),
      codec: 'JPEG',
      framesDecoded: counters.shown,
      framesDropped: counters.skipped,
      framesReceived: counters.received,
    };
  }

  const { pcm, pcmCounters } = s.media;
  const audioBits = rateOf(s, 'audioBits', pcmCounters.bytes * 8, now);
  const audio = pcmCounters.blocks === 0 ? null : {
    codec: 'PCM 16-bit, 8 kHz',
    bitsPerSecond: roundTo(audioBits, 0),
    packetsReceived: pcmCounters.blocks,
    totalSamplesReceived: pcmCounters.samples,
    bufferedSeconds: roundTo(pcm?.buffered, 4),
    contextState: pcm?.context.state ?? null,
  };

  const level = pcmCounters.level;
  pcmCounters.level *= 0.5; // dies away unless new audio raises it

  return {
    sources,
    audio,
    level,
    connection: { transport: 'WebSocket', route: s.url, bytesReceived: s.bytesReceived, connectedAt: s.openedAt },
  };
}

/** H.264 mode: the WebRTC connection's own statistics. Null if the connection goes away meanwhile. */
async function measurePeer(s) {
  const peer = s.rtc.peer;
  let report;
  try {
    report = await peer.getStats();
  } catch {
    return null;
  }
  if (session !== s || s.rtc.peer !== peer) {
    return null;
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

  const sources = {};
  for (const video of videos) {
    const id = s.rtc.trackToSource.get(video.trackIdentifier);
    if (!id) {
      continue;
    }
    sources[id] = {
      width: video.frameWidth,
      height: video.frameHeight,
      framesPerSecond: video.framesPerSecond,
      bitsPerSecond: roundTo(rateOf(s, `video:${id}`, video.bytesReceived * 8, video.timestamp), 0),
      codec: describeCodec(stats.get(video.codecId)),
      framesDecoded: video.framesDecoded,
      framesDropped: video.framesDropped,
      keyFramesDecoded: video.keyFramesDecoded,
      packetsReceived: video.packetsReceived,
      packetsLost: video.packetsLost,
      jitterMs: wholeMilliseconds(video.jitter),
    };
  }

  let audioStats = null;
  if (audio && role.playsAudio) {
    audioStats = {
      codec: describeCodec(stats.get(audio.codecId)),
      bitsPerSecond: roundTo(rateOf(s, 'audio', audio.bytesReceived * 8, audio.timestamp), 0),
      packetsReceived: audio.packetsReceived,
      packetsLost: audio.packetsLost,
      // The jitter buffer's delay since the last poll, not over the connection's life: like the
      // PCM queue in JPEG mode, it says how things are now.
      bufferedSeconds: roundTo(averageSinceLastPoll(s, 'audioDelay', audio.jitterBufferDelay, audio.jitterBufferEmittedCount), 4),
      totalSamplesReceived: audio.totalSamplesReceived,
      jitterMs: wholeMilliseconds(audio.jitter),
      audioLevel: audio.audioLevel,
    };
  }

  return {
    state: peer.connectionState,
    sources,
    audio: audioStats,
    level: audioStats?.audioLevel ?? 0,
    connection: {
      transport: `WebRTC (${peer.connectionState})`,
      route: pair ? `${describeCandidate(stats.get(pair.localCandidateId))} → ${describeCandidate(stats.get(pair.remoteCandidateId))}` : undefined,
      roundTripSeconds: pair?.currentRoundTripTime,
      bytesReceived: pair?.bytesReceived,
      connectedAt: s.rtc.connectedAt,
    },
  };
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
    if (session) {
      session.controlError = 'The frame rate has to be a whole number from 1 to 60.';
      render();
    }
  }
});

function setMuted(value) {
  muted = value;
  el.webrtcAudio.muted = value;
  session?.media.pcm?.setMuted(value);
  if (!value) {
    session?.media.pcm?.resume();
    if (el.webrtcAudio.srcObject) {
      el.webrtcAudio.play().catch(() => {});
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

// A pop-out shows nothing but its stream.
document.body.classList.toggle('popout', !role.hasControls);
document.title = role.titleFor(null) ?? document.title;

buildStatPanels();
render();

// ?url=ws://host:port/ presets the address; ?autoconnect connects on load.
el.url.value = parameters.get('url') || role.savedAddress() || DEFAULT_URL;

if (parameters.has('autoconnect')) {
  connect();
}
