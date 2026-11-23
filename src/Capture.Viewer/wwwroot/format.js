// How the viewer shows numbers: bit rates, sizes, durations, codec names.

/** Shown where there is no value. */
export const EMPTY = '–';

export function formatBitRate(bitsPerSecond) {
  if (bitsPerSecond === undefined) {
    return EMPTY;
  }
  return bitsPerSecond >= 1_000_000
    ? `${(bitsPerSecond / 1_000_000).toFixed(2)} Mbit/s`
    : `${Math.round(bitsPerSecond / 1000)} kbit/s`;
}

export function formatFrameRate(framesPerSecond) {
  return framesPerSecond === undefined ? EMPTY : `${framesPerSecond} fps`;
}

export function formatMilliseconds(seconds) {
  return seconds === undefined ? EMPTY : `${(seconds * 1000).toFixed(1)} ms`;
}

export function formatBytes(bytes) {
  if (bytes === undefined) {
    return EMPTY;
  }
  return bytes >= 1_048_576 ? `${(bytes / 1_048_576).toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`;
}

export function formatDuration(milliseconds) {
  const total = Math.floor(milliseconds / 1000);
  const hours = Math.floor(total / 3600);
  const minutes = String(Math.floor((total % 3600) / 60)).padStart(2, '0');
  const seconds = String(total % 60).padStart(2, '0');
  return hours > 0 ? `${hours}:${minutes}:${seconds}` : `${minutes}:${seconds}`;
}

export function formatCount(value) {
  return value === undefined ? EMPTY : value.toLocaleString();
}

/** A WebRTC codec as people know it: "video/H264" + "profile-level-id=42e01f" is "H.264 Constrained Baseline". */
export function describeCodec(codec) {
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
