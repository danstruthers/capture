// The capture client's WebSocket protocol, as the viewer uses it. The capture client's side is
// src/Capture/Output/ViewerProtocol.cs; README.md ("WebSocket protocol") describes both.
//
// Text messages are JSON:
//   capture -> viewer   {"type":"state", remoteControl, layout, recording, streaming, video, sources, audio}
//                       {"type":"error","message":...,"request":<id of the refused request, if it had one>}
//                       {"type":"offer","sdp":...,"sources":{"<mid>":"<stream id>",...}}  and ICE candidates
//   viewer -> capture   select, command and configure requests (built below), the WebRTC answer and ICE candidates
//
// The state's audio.devices lists every audio device the capture client can record: recording
// devices ("input") and playback devices ("output", recorded as what they play), each with its id,
// whether it is selected and how it is doing. "default-input" and "default-output" stand for
// Windows' default devices and follow them when they change.
//
// Media arrives in one of two ways, chosen by the capture client's video mode:
//   h264  a WebRTC session offered by the capture client: one H.264 track per stream, G.711 audio
//   jpeg  binary messages on the socket (see parseMediaMessage)

export const PICTURE_MESSAGE = 1;
export const AUDIO_MESSAGE = 2;
const MEDIA_HEADER_LENGTH = 10;

/** The audio device entries that stand for Windows' default recording and playback device. */
export const DEFAULT_DEVICE_IDS = ['default-input', 'default-output'];

export function isDefaultDeviceEntry(device) {
  return DEFAULT_DEVICE_IDS.includes(device.id);
}

/**
 * A binary message from the capture client (JPEG mode):
 * [type: 1 byte][stream number: 1 byte][timestamp: int64 little-endian, 100 ns units][payload].
 * Type 1 is a JPEG picture of the stream with that number; type 2 is 16-bit little-endian PCM,
 * mono, at the state's audio.sampleRate. Null if it is too short to be one.
 * @param {ArrayBuffer} buffer
 * @returns {{type: number, number: number, payload: Uint8Array} | null}
 */
export function parseMediaMessage(buffer) {
  if (buffer.byteLength < MEDIA_HEADER_LENGTH) {
    return null;
  }
  const header = new DataView(buffer);
  return { type: header.getUint8(0), number: header.getUint8(1), payload: new Uint8Array(buffer, MEDIA_HEADER_LENGTH) };
}

/** The PCM samples of an audio message's payload. */
export function pcmSamples(payload) {
  return new Int16Array(payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.byteLength));
}

/** What a viewer window wants to be sent: these streams (ids), and the sound or not. */
export function selectMessage(sources, audio) {
  return { type: 'select', sources, audio };
}

/** Start, stop, pause or resume "recording" or "streaming". */
export function commandMessage(target, action) {
  return { type: 'command', target, action };
}

/** Changes settings: frameRate, videoMode, layout, audioDevices. */
export function configureMessage(settings) {
  return { type: 'configure', ...settings };
}

/** The capture client's address with the selection of a pop-out window, so that it is never sent anything else. */
export function addressFor(url, onlySource) {
  const address = new URL(url);
  address.searchParams.set('select', onlySource);
  address.searchParams.set('audio', '0');
  return address.href;
}
