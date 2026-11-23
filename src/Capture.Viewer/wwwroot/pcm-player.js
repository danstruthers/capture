// Playback of the raw PCM audio the capture client sends in JPEG mode.

/**
 * Plays blocks of 16-bit PCM as they arrive. Each block is scheduled to start exactly where the
 * previous one ends, a little behind the moment of arrival so that late blocks leave no gap.
 */
export class PcmPlayer {
  /**
   * @param {number} sampleRate the stream's rate (the state's audio.sampleRate)
   * @param {boolean} muted whether to start silent
   */
  constructor(sampleRate, muted) {
    this.sampleRate = sampleRate;
    try {
      // A context running at the stream's own rate plays consecutive blocks sample-exactly.
      this.context = new AudioContext({ sampleRate, latencyHint: 'interactive' });
    } catch {
      this.context = new AudioContext({ latencyHint: 'interactive' });
    }
    this.gain = this.context.createGain();
    this.gain.connect(this.context.destination);
    this.nextTime = 0;
    this.setMuted(muted);
  }

  /** Whether the browser is holding the sound back until someone clicks the page. */
  get suspended() {
    return this.context.state === 'suspended';
  }

  /** Seconds of audio scheduled but not yet played. */
  get buffered() {
    return Math.max(0, this.nextTime - this.context.currentTime);
  }

  /** @param {Int16Array} samples */
  play(samples) {
    const buffer = this.context.createBuffer(1, samples.length, this.sampleRate);
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
