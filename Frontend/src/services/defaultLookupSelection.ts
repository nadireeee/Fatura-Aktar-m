import type { ISourceBranchDto, ISourceDepotDto, ISourcePeriodDto } from '../types';

const norm = (s: string | undefined) => (s ?? '').trim().toUpperCase();

function matchesMerkez(name: string | undefined): boolean {
  const n = norm(name);
  if (!n) return false;
  return n === 'MERKEZ' || n.startsWith('MERKEZ ') || n.endsWith(' MERKEZ') || n.includes(' MERKEZ ');
}

/**
 * Dönem: tek kayıt → o; çok kayıt → tercih edilen kaynak dönem (varsa); yoksa öntanımlı; yoksa listedeki ilk.
 */
export function chooseDefaultPeriod(
  periods: ISourcePeriodDto[],
  preferredDonemKodu?: number
): ISourcePeriodDto | null {
  if (!periods.length) return null;
  if (periods.length === 1) return periods[0];
  if (preferredDonemKodu != null && preferredDonemKodu > 0) {
    const m = periods.find(x => x.donemkodu === preferredDonemKodu);
    if (m) return m;
  }
  const ont = periods.find(p => p.ontanimli);
  if (ont) return ont;
  return periods[0];
}

/** Hedef firma: fatura tarihi dönem aralığına giriyorsa o satır (kaynak donem_kodu eşlemesi değil). */
export function choosePeriodByInvoiceDate(
  periods: ISourcePeriodDto[],
  invoiceDateStr?: string | null
): ISourcePeriodDto | null {
  if (!periods.length || !invoiceDateStr?.trim()) return null;
  const t = Date.parse(invoiceDateStr.trim());
  if (Number.isNaN(t)) return null;
  const inv = new Date(t);
  inv.setHours(0, 0, 0, 0);
  for (const p of periods) {
    const bs = p.baslangic_tarihi?.trim();
    const es = p.bitis_tarihi?.trim();
    if (!bs || !es) continue;
    const b = Date.parse(bs);
    const e = Date.parse(es);
    if (Number.isNaN(b) || Number.isNaN(e)) continue;
    const B = new Date(b);
    const E = new Date(e);
    B.setHours(0, 0, 0, 0);
    E.setHours(0, 0, 0, 0);
    if (inv >= B && inv <= E) return p;
  }
  return null;
}

/** Hedef dönem önerisi: önce tarih, sonra öntanımlı/ilk (kaynak kodunu hedefe zorlamaz). */
export function chooseDefaultTargetPeriod(
  periods: ISourcePeriodDto[],
  invoiceDateStr?: string | null
): ISourcePeriodDto | null {
  const byDate = choosePeriodByInvoiceDate(periods, invoiceDateStr);
  if (byDate) return byDate;
  return chooseDefaultPeriod(periods, undefined);
}

/** Şube: tek kayıt → o; çok kayıt → MERKEZ adı, yoksa ilk kayıt. */
export function chooseDefaultBranch(branches: ISourceBranchDto[]): ISourceBranchDto | null {
  if (!branches.length) return null;
  if (branches.length === 1) return branches[0];
  const merkez = branches.find(b => matchesMerkez(b.subeadi));
  if (merkez) return merkez;
  return branches[0];
}

/** Depo: tek kayıt → o; çok kayıt → MERKEZ adı, yoksa ilk kayıt. */
export function chooseDefaultDepot(depots: ISourceDepotDto[]): ISourceDepotDto | null {
  if (!depots.length) return null;
  if (depots.length === 1) return depots[0];
  const merkez = depots.find(d => matchesMerkez(d.depoadi));
  if (merkez) return merkez;
  return depots[0];
}
