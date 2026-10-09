/** Disjoint sets over a fixed id list; a join naming an id outside the list is ignored. */
export class UnionFind {
  private parent = new Map<number, number>();

  constructor(ids: number[]) {
    for (const id of ids) this.parent.set(id, id);
  }

  find(id: number): number {
    let root = id;
    while (this.parent.get(root) !== root) root = this.parent.get(root)!;
    let cur = id;
    while (cur !== root) {
      const next = this.parent.get(cur)!;
      this.parent.set(cur, root);
      cur = next;
    }
    return root;
  }

  join(a: number, b: number): void {
    if (!this.parent.has(a) || !this.parent.has(b)) return;
    this.parent.set(this.find(a), this.find(b));
  }

  groups(ids: number[]): number[][] {
    const out = new Map<number, number[]>();
    for (const id of ids) {
      const r = this.find(id);
      if (!out.has(r)) out.set(r, []);
      out.get(r)!.push(id);
    }
    return [...out.values()];
  }
}
