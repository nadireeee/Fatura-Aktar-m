/**
 * Backend `LookupNormalizer` ile aynı mantık: tek tip firma/şube/depo ağacı.
 * API'den gelen ham obje (camelCase veya snake) kabul eder.
 */
export type NormalizedDonem = {
  key: number;
  donemKodu: number;
  gorunenKod: string;
  ontanimli: boolean;
  baslangicTarihi: string | null;
  bitisTarihi: string | null;
};

export type NormalizedDepo = { key: number; depoAdi: string };
export type NormalizedSube = { key: number; subeAdi: string; depolar: NormalizedDepo[] };

export type CompanyLookupNormalized = {
  firmaKodu: number;
  firmaAdi: string;
  donemler: NormalizedDonem[];
  subeler: NormalizedSube[];
};

function num(v: unknown, fallback = 0): number {
  const n = Number(v);
  return Number.isFinite(n) ? n : fallback;
}

function str(v: unknown): string {
  if (v == null) return '';
  return String(v).trim();
}

/** Ham firma kaydından (authorized tree veya API) normalize edilmiş ağaç üretir. */
export function normalizeCompanyLookup(raw: Record<string, unknown>): CompanyLookupNormalized {
  const firmaKodu = num(raw.firmaKodu ?? raw.firma_kodu, 0);
  const firmaAdi = str(raw.firmaAdi ?? raw.firma_adi);

  const donemlerRaw = (raw.donemler ?? raw.Donemler) as unknown;
  const arrDonem = Array.isArray(donemlerRaw) ? donemlerRaw : [];
  const donemler: NormalizedDonem[] = arrDonem.map((d: Record<string, unknown>) => ({
    key: num(d.key ?? d.Key, 0),
    donemKodu: num(d.donemKodu ?? d.donemkodu, 0),
    gorunenKod: str(d.gorunenKod ?? d.gorunenkod ?? d.GorunenKod),
    ontanimli: Boolean(d.ontanimli ?? d.Ontanimli),
    baslangicTarihi: str(d.baslangicTarihi ?? d.baslangic_tarihi) || null,
    bitisTarihi: str(d.bitisTarihi ?? d.bitis_tarihi) || null,
  }));

  const subelerRaw = (raw.subeler ?? raw.Subeler) as unknown;
  const arrSube = Array.isArray(subelerRaw) ? subelerRaw : [];
  const subeler: NormalizedSube[] = arrSube.map((s: Record<string, unknown>) => {
    const depRaw = (s.depolar ?? s.Depolar) as unknown;
    const depArr = Array.isArray(depRaw) ? depRaw : [];
    const depolar: NormalizedDepo[] = depArr.map((dep: Record<string, unknown>) => ({
      key: num(dep.key ?? dep.Key, 0),
      depoAdi: str(dep.depoAdi ?? dep.depoadi ?? dep.DepoAdi),
    }));
    return {
      key: num(s.key ?? s.Key, 0),
      subeAdi: str(s.subeAdi ?? s.subeadi ?? s.SubeAdi),
      depolar,
    };
  });

  return { firmaKodu, firmaAdi, donemler, subeler };
}

export function formatCompanyOptionLabel(firmaKodu: number, firmaAdi: string): string {
  const name = firmaAdi || `Firma ${firmaKodu}`;
  return `${firmaKodu} — ${name}`;
}

/** Kaynak / görüntüleme dönem dropdown: yıl veya aralık; çıplak donemkodu göstermez. */
export function formatKaynakDonemLabel(p: {
  gorunenkod?: string;
  baslangic_tarihi?: string;
  bitis_tarihi?: string;
  donemkodu?: number;
}): string {
  const a = (p.baslangic_tarihi ?? '').trim();
  const b = (p.bitis_tarihi ?? '').trim();
  const da = parseDiaDateLoose(a);
  const db = parseDiaDateLoose(b);
  if (da && db) {
    if (da.getFullYear() === db.getFullYear()) return String(da.getFullYear());
    return `${da.getFullYear()}–${db.getFullYear()}`;
  }
  if (da) return String(da.getFullYear());
  const g = (p.gorunenkod ?? '').trim();
  if (g) return g;
  if (p.donemkodu != null && p.donemkodu > 0) return `Dönem · ${p.donemkodu}`;
  return 'Dönem';
}

function parseDiaDateLoose(raw: string): Date | null {
  if (!raw) return null;
  const t = raw.trim();
  const iso = Date.parse(t);
  if (!Number.isNaN(iso)) return new Date(iso);
  const m = t.match(/^(\d{1,2})\.(\d{1,2})\.(\d{4})/);
  if (m) return new Date(Number(m[3]), Number(m[2]) - 1, Number(m[1]));
  return null;
}
