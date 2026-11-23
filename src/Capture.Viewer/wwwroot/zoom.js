// Zooming into a picture: the mouse wheel zooms around the pointer, dragging moves, double-click resets.

const MAX_ZOOM = 8;

export class Zoom {
  /**
   * @param {HTMLElement} view the element the picture fills; it takes the wheel and pointer events
   * @param {HTMLElement[]} targets the elements scaled and moved (the video and the canvas)
   * @param {(scale: number) => void} onChange told the scale after every change
   */
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
