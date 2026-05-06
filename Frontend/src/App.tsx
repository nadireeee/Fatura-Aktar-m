import { useEffect, useState, useMemo, useCallback, useRef } from 'react';
import { InvoiceService } from './services/api';
import type {
  IInvoiceListRow,
  IInvoiceLineListRow,
  ITransferLogEntry,
  ISourceCompanyDto,
  ISourceBranchDto,
  ISourceDepotDto,
  ISourcePeriodDto,
} from './types';
import './index.css';
import { extractRows } from './rpr/extractRows';
import { normalizeRprRow, type NormalizedRprRow } from './rpr/normalize';

/** RPR/client tarih alanları (ISO, dd.MM.yyyy) — filtre için güvenilir sıralama */
function parseFlexibleDateMs(d: unknown): number {
  const s = String(d ?? '').trim();
  if (!s) return NaN;
  const ymd = s.slice(0, 10);
  if (/^\d{4}-\d{2}-\d{2}$/.test(ymd)) {
    const t = Date.parse(ymd);
    if (!Number.isNaN(t)) return t;
  }
  const m = /^(\d{1,2})[./-](\d{1,2})[./-](\d{4})/.exec(s);
  if (m) {
    const dd = Number(m[1]);
    const mm = Number(m[2]);
    const yyyy = Number(m[3]);
    if (mm >= 1 && mm <= 12 && dd >= 1 && dd <= 31) return new Date(yyyy, mm - 1, dd).getTime();
  }
  const t2 = Date.parse(s);
  return Number.isNaN(t2) ? NaN : t2;
}

/** RPR satırından tek POST aktarım için başlık snapshot (scf_fatura_getir yerine). */
/** Başlık döviz kuru — DİA 6 ondalık invariant. */
function formatHeaderKur6(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return '';
  return (Math.round(n * 1e6) / 1e6).toFixed(6);
}

function buildHeaderSnapshotFromInvoice(allRows: NormalizedRprRow[], invoiceKey: number): Record<string, unknown> | undefined {
  const ik = Number(invoiceKey);
  const sameInv = allRows.filter(r => Number(r.invoiceKey) === ik);
  if (sameInv.length === 0) return undefined;
  const row = sameInv.find(r => !r.invalid) ?? sameInv[0];
  const o = row.sourceRow as Record<string, unknown>;
  const pickStr = (keys: string[]) => {
    for (const k of keys) {
      const s = String(o[k] ?? '').trim();
      if (s) return s;
    }
    return '';
  };
  const time = pickStr(['saat', 'SAAT']) || '12:00:00';
  /** Fiş no / fatura no: raporda biri eksikse diğeriyle doldur (çoğu tenant tek numara kullanır). */
  let invoiceNo =
    String(row.invoiceNo ?? '').trim() ||
    pickStr(['fatura_no', 'FATURA_NO', 'belgeno2', 'BELGENO2', 'belgeno', 'BELGENO']);
  let fisNo =
    String(row.fisNo ?? '').trim() ||
    pickStr(['fisno', 'FISNO', 'fis_no', 'FIS_NO', 'fis_nosu']) ||
    pickStr(['snapshot_fis_no', 'SNAPSHOT_FIS_NO']);
  // SIKI KURAL: Fatura No ve Fiş No ayrı alanlar. Biri yoksa diğeriyle doldurmayız.
  let cariCode =
    pickStr(['snapshot_cari_kodu', 'SNAPSHOT_CARI_KODU']) ||
    String(row.cariCode ?? '').trim() ||
    pickStr([
      'carikartkodu',
      '__carikartkodu',
      'CARIKARTKODU',
      'carikodu',
      'CARIKODU',
      'cari_kodu',
      'CARI_KODU',
      'hesapkodu',
      'HESAPKODU',
      'hesap_kodu',
      'fk.carikartkodu',
      'fk.CARIKARTKODU',
      'fk.hesapkodu',
      'fk.HESAPKODU',
    ]);
  if (!cariCode) {
    const alt = sameInv.map(r => String(r.cariCode ?? '').trim()).find(Boolean);
    if (alt) cariCode = alt;
  }
  let date =
    pickStr(['snapshot_iso_date', 'SNAPSHOT_ISO_DATE']) ||
    String(row.date ?? '').trim() ||
    pickStr([
      'tarih',
      'TARIH',
      'fatura_tarihi',
      'FATURA_TARIHI',
      'fk.tarih',
      'fk.TARIH',
    ]);
  if (!date) {
    const altD = sameInv.map(r => String(r.date ?? '').trim()).find(Boolean);
    if (altD) date = altD;
  }
  let invoiceTypeCode = row.invoiceTypeCode > 0 ? row.invoiceTypeCode : 0;
  if (!(invoiceTypeCode > 0)) {
    const tr = row.invoiceTypeRaw as unknown;
    if (typeof tr === 'number' && Number.isFinite(tr) && tr > 0) invoiceTypeCode = tr;
    else if (tr != null && String(tr).trim() !== '') {
      const n = Number(tr);
      if (Number.isFinite(n) && n > 0) invoiceTypeCode = n;
    }
  }
  if (!(invoiceTypeCode > 0)) {
    const sn = Number(o?.snapshot_invoice_type_num ?? o?.SNAPSHOT_INVOICE_TYPE_NUM);
    if (Number.isFinite(sn) && sn > 0) invoiceTypeCode = sn;
  }
  if (!(invoiceTypeCode > 0)) {
    const raw = o?.fatura_turu ?? o?.FATURA_TURU ?? o?.turu ?? o?.TURU ?? o?.['fk.turu'] ?? o?.['fk.TURU'];
    const n = Number(raw);
    if (Number.isFinite(n) && n > 0) invoiceTypeCode = n;
  }
  if (!(invoiceTypeCode > 0)) {
    const altT = sameInv.map(r => r.invoiceTypeCode).find(c => c > 0);
    if (altT) invoiceTypeCode = altT;
  }
  const currencyCode =
    pickStr(['snapshot_currency_kodu', 'SNAPSHOT_CURRENCY_KODU']) ||
    String(row.invoiceCurrencyCode ?? '').trim() ||
    String(row.currencyCode ?? '').trim() ||
    pickStr([
      'currencyCode',
      'CURRENCYCODE',
      'dovizkodu',
      'DOVIZKODU',
      'dovizadi',
      'doviz',
      '__anadovizadi',
      'fk.dovizkodu',
      'fk.DOVIZKODU',
    ]);
  const rateForHeader = (() => {
    const fromRows = sameInv.map(r => r.invoiceExchangeRate).find(x => Number.isFinite(x) && x > 0);
    if (fromRows != null && fromRows > 0) return fromRows;
    if (Number.isFinite(row.invoiceExchangeRate) && row.invoiceExchangeRate > 0) return row.invoiceExchangeRate;
    if (Number.isFinite(row.exchangeRate) && row.exchangeRate > 0) return row.exchangeRate;
    return 0;
  })();
  const headerKurStr = formatHeaderKur6(Number(rateForHeader) || 0);
  const rawYetkili = pickStr([
    'yetkikodu',
    'YETKIKODU',
    'cari_yetkili_kodu',
    'CARI_YETKILI_KODU',
    'yetkili_kodu',
    'YETKILI_KODU',
    'snapshot_yetkili_kodu',
    'fk.yetkikodu',
  ]);
  const cariYetkiliKodu = (() => {
    const t = String(rawYetkili ?? '').trim();
    if (!t || t.length > 64) return '';
    const tl = t.toLowerCase();
    if (['test', 'deneme', 'yok', 'null', '-', '.', '0', ','].includes(tl)) return '';
    if (/^\d{10,}$/.test(t)) return '';
    return t;
  })();
  return {
    sourceInvoiceKey: invoiceKey,
    invoiceNo,
    fisNo,
    date,
    time,
    invoiceType: row.invoiceTypeLabel,
    invoiceTypeCode: invoiceTypeCode > 0 ? invoiceTypeCode : undefined,
    upperProcessCode: row.upperProcessCode,
    cariCode,
    cariName: row.cariName,
    currencyCode,
    exchangeRate: rateForHeader > 0 ? rateForHeader : row.exchangeRate,
    ...(headerKurStr
      ? { headerDovizKuru: headerKurStr, headerRaporlamaDovizKuru: headerKurStr }
      : {}),
    sourceBranchName: row.sourceBranchName,
    sourceWarehouseName: row.sourceWarehouseName,
    total: row.invoiceTotal,
    discount: row.invoiceDiscountTotal,
    expense: row.invoiceExpenseTotal,
    vat: row.invoiceVat,
    net: row.invoiceNet,
    ...(cariYetkiliKodu ? { cariYetkiliKodu } : {}),
  };
}

/** Son aktarılan satırda havuz cari kodu: snapshot + ham RPR satırı anahtarları. */
function pickPoolCariKodFromTransfer(
  hs: Record<string, unknown> | undefined,
  poolRow: { cariCode?: string; sourceRow?: unknown } | undefined
): string {
  const raw = poolRow?.sourceRow as Record<string, unknown> | undefined;
  const fromHs = [
    hs?.cariCode,
    hs?.CariCode,
    hs?.carikartkodu,
    hs?.Carikartkodu,
    hs?.CARIKARTKODU,
    hs?.hesapkodu,
    hs?.Hesapkodu,
    hs?.HESAPKODU,
  ];
  const fromRaw = [
    raw?.carikartkodu,
    raw?.CARIKARTKODU,
    raw?.hesapkodu,
    raw?.HESAPKODU,
    raw?.cari_kodu,
    raw?.CARI_KODU,
    raw?.carikodu,
    raw?.CARIKODU,
  ];
  for (const c of [...fromHs, ...fromRaw, poolRow?.cariCode]) {
    const t = String(c ?? '').trim();
    if (t) return t;
  }
  return '';
}

/** Havuzda aynı fatura: ilk kalem sırası (başlık tutarları) + cari kodu dolu satır. */
function pickInvoiceRowsForTransferDisplay(allRows: NormalizedRprRow[], invKey: number) {
  const rows = allRows.filter(r => Number(r.invoiceKey) === invKey && !r.invalid);
  const sorted = [...rows].sort((a, b) => Number(a.lineKey) - Number(b.lineKey));
  const meta = sorted[0];
  const cariRow = sorted.find(r => String(r.cariCode ?? '').trim()) ?? meta;
  return { meta, cariRow };
}

/** Son aktarılan tab: NET — headerSnapshot + havuz satırı; uçuk değerlerde toplam−KDV’ye düş. */
function pickPoolNetForLastTransfer(
  hs: Record<string, unknown> | undefined,
  poolRow: { invoiceNet?: number; invoiceTotal?: number; invoiceVat?: number } | undefined
): number | undefined {
  const sane = (n: number) => Number.isFinite(n) && n > 0 && n < 1e15;
  const fromHs = Number(hs?.net ?? hs?.Net);
  if (sane(fromHs)) {
    const t = Number(poolRow?.invoiceTotal);
    const n = Number(poolRow?.invoiceNet);
    if (Number.isFinite(t) && t > 0 && fromHs > t * 100 && Number.isFinite(n) && n > 0 && n <= t * 2) return n;
    return fromHs;
  }

  const t = Number(poolRow?.invoiceTotal);
  const v = Number(poolRow?.invoiceVat ?? 0);
  const n = Number(poolRow?.invoiceNet);
  if (Number.isFinite(n) && n > 0) {
    if (Number.isFinite(t) && t > 0 && n > t * 50) {
      const approx = t - v;
      if (Number.isFinite(approx) && approx >= 0 && approx <= t * 1.05) return approx;
    }
    if (sane(n)) return n;
  }
  if (Number.isFinite(t) && sane(t)) return Math.max(0, t - (Number.isFinite(v) ? v : 0));
  return undefined;
}

function transferStatusCellClass(label: string): string {
  const v = String(label || '').trim().toLowerCase();
  if (v.includes('aktarıldı')) return 'erp-transfer-st erp-transfer-st-done';
  if (v.includes('kısmi')) return 'erp-transfer-st erp-transfer-st-partial';
  if (v.includes('bekle')) return 'erp-transfer-st erp-transfer-st-wait';
  return 'erp-transfer-st erp-transfer-st-other';
}

function computeInvoiceTransferStatus(totalKalemCount: number, transferredCount: number): string {
  if (totalKalemCount <= 0) return '';
  if (transferredCount <= 0) return 'Bekliyor';
  if (transferredCount >= totalKalemCount) return 'Aktarıldı';
  return 'Kısmi';
}

/** Son aktarılan satırı: havuz tablosu kolonlarıyla aynı alanları doldur. */
function poolMetaToLastTransferGrid(poolMeta: NormalizedRprRow | undefined) {
  if (!poolMeta) return {};
  const o: {
    poolUpperProcess?: string;
    poolKaynakSube?: string;
    poolKaynakDepo?: string;
    poolDoviz?: string;
    poolToplam?: number;
    poolIndirim?: number;
    poolMasraf?: number;
    poolKdv?: number;
    poolVeri?: string;
    poolTransferStatus?: string;
    poolKalemSube?: string;
  } = {};
  const up = String(poolMeta.upperProcessName || poolMeta.upperProcessCode || '').trim();
  if (up) o.poolUpperProcess = up;
  const ks = String(poolMeta.sourceBranchName ?? '').trim();
  if (ks) o.poolKaynakSube = ks;
  const kd = String(poolMeta.sourceWarehouseName ?? '').trim();
  if (kd) o.poolKaynakDepo = kd;
  const dv = String(poolMeta.invoiceCurrencyCode || poolMeta.currencyCode || '').trim();
  if (dv) o.poolDoviz = dv;
  const toplam = Number(poolMeta.invoiceTotal);
  if (Number.isFinite(toplam)) o.poolToplam = toplam;
  const ind = Number(poolMeta.invoiceDiscountTotal);
  if (Number.isFinite(ind)) o.poolIndirim = ind;
  const mas = Number(poolMeta.invoiceExpenseTotal);
  if (Number.isFinite(mas)) o.poolMasraf = mas;
  const kdv = Number(poolMeta.invoiceVat);
  if (Number.isFinite(kdv)) o.poolKdv = kdv;
  const veri = String(poolMeta.snapshotErrorSummary ?? '').trim();
  if (veri) o.poolVeri = veri;
  const ts = String(poolMeta.transferStatus ?? '').trim();
  if (ts) o.poolTransferStatus = ts;
  const dyn = String(poolMeta.dynamicBranch ?? '').trim();
  if (dyn) o.poolKalemSube = dyn;
  return o;
}

/** Tek fatura POST gövdesi (fatura-aktar içindeki invoices[] öğesi). Doğrulama backend’dedir. */
type InvoiceTransferPayload = {
  sourceInvoiceKey: number;
  selectedKalemKeys: number[];
  headerSnapshot?: Record<string, unknown>;
  selectedLineSnapshots?: any[];
};

/** Backend InvoiceTransferService.TryValidateRawSnapshot ile aynı satır kuralları (RAW fail-fast). */
type RawLineIssue = { lineIndex: number; reason: string };

function collectRawLineSnapshotIssues(
  selectedLineSnapshots: unknown[] | undefined,
  headerSnapshot: Record<string, unknown>
): RawLineIssue[] {
  const issues: RawLineIssue[] = [];
  const headerDoviz = Number(headerSnapshot.targetSisDovizKey);
  const headerDovizOk = Number.isFinite(headerDoviz) && headerDoviz > 0;

  if (!selectedLineSnapshots || selectedLineSnapshots.length === 0) {
    issues.push({ lineIndex: -1, reason: 'selectedLineSnapshots boş (RAW zorunlu)' });
    return issues;
  }

  selectedLineSnapshots.forEach((raw, i) => {
    const lineNo = i + 1;
    if (raw == null || typeof raw !== 'object') {
      issues.push({ lineIndex: lineNo, reason: 'satır null' });
      return;
    }
    const l = raw as Record<string, unknown>;
    const num = (k: string) => {
      const x = Number(l[k]);
      return Number.isFinite(x) ? x : NaN;
    };
    const tkt = num('targetKeyKalemTuru');
    const tkb = num('targetKeyKalemBirim');
    const tsd = num('targetKeySisDoviz');
    const lineDovizOk = (Number.isFinite(tsd) && tsd > 0) || headerDovizOk;
    if (!(tkt > 0)) issues.push({ lineIndex: lineNo, reason: 'targetKeyKalemTuru>0 (kalem türü _key)' });
    if (!(tkb > 0)) issues.push({ lineIndex: lineNo, reason: 'targetKeyKalemBirim>0 (birim _key)' });
    if (!lineDovizOk) issues.push({ lineIndex: lineNo, reason: 'targetKeySisDoviz veya başlık targetSisDovizKey' });
    const mq = num('miktar');
    if (!(mq > 0)) issues.push({ lineIndex: lineNo, reason: 'miktar>0' });
    const bf = l.birimFiyati;
    if (bf === undefined || bf === null || Number.isNaN(Number(bf)) || Number(bf) < 0) {
      issues.push({ lineIndex: lineNo, reason: 'birimFiyati>=0' });
    }
    const tut = l.tutar;
    if (tut === undefined || tut === null || Number.isNaN(Number(tut))) {
      issues.push({ lineIndex: lineNo, reason: 'tutar zorunlu (RAW sunucu hesaplamaz)' });
    }
    const kt = String(l.kalemTuru ?? '').trim();
    if (!kt) issues.push({ lineIndex: lineNo, reason: 'kalemTuru MLZM/HZMT zorunlu' });
  });

  return issues;
}

/** Birim eşleşmesi: ADET / AD / "Adet." / " Adet " farklarını yumuşatır. */
function normalizeBirimLookupKey(s: string): string {
  return String(s ?? '')
    .trim()
    .replace(/^[.,;:]+/g, '')
    .replace(/[.,;:]+$/g, '')
    .toUpperCase()
    .replace(/\s+/g, '');
}

/** Liste ve RPR arasında yaygın birim alias’ları (tek yön — tam eşleşme sonrası fallback). */
const RAW_BIRIM_ALIAS_TO_CANON: Record<string, string> = {
  AD: 'ADET',
  ADT: 'ADET',
  UNIT: 'ADET',
};

function augmentBirimLookupMap(birimNormToKey: Map<string, number>): void {
  const snap = [...birimNormToKey.entries()];
  for (const [norm, key] of snap) {
    if (!(key > 0)) continue;
    if (norm === 'ADET') {
      if (!birimNormToKey.has('AD')) birimNormToKey.set('AD', key);
    }
  }
}

function resolveBirimKeyFromRawMaps(birimNormToKey: Map<string, number>, birimAdi: string): number | undefined {
  const raw = String(birimAdi ?? '').trim();
  const candidates: string[] = [];
  const push = (s: string) => {
    const n = normalizeBirimLookupKey(s);
    if (n) candidates.push(n);
  };
  push(raw);
  push(raw.replace(/\.$/g, ''));
  push(raw.replace(/[.,;:]+$/g, ''));
  const seen = new Set<string>();
  for (const c of candidates) {
    if (seen.has(c)) continue;
    seen.add(c);
    const hit = birimNormToKey.get(c);
    if (hit != null && hit > 0) return hit;
    const canon = RAW_BIRIM_ALIAS_TO_CANON[c];
    if (canon) {
      const h2 = birimNormToKey.get(canon);
      if (h2 != null && h2 > 0) return h2;
    }
  }
  return undefined;
}

type RawLineLookupMaps = {
  birimNormToKey: Map<string, number>;
  kalemTuruUpperToKey: Map<string, number>;
};

let rawLineLookupHttpCache: { cacheKey: string; maps: RawLineLookupMaps } | null = null;

/** DİA listesinde kod "MALZEME"/"HİZMET" iken infer MLZM/HZMT ile eşleşsin diye ek anahtarlar. */
function augmentKalemTuruLookupMap(map: Map<string, number>): void {
  const snap = [...map.entries()];
  for (const [rawKod, key] of snap) {
    if (!(key > 0)) continue;
    const kod = rawKod.trim();
    const uTr = kod.toLocaleUpperCase('tr-TR');
    const uEn = kod.toUpperCase();
    const hzmtHint =
      uEn.includes('HZMT') ||
      uTr.includes('HİZMET') ||
      uEn.includes('HIZMET') ||
      uTr.includes('SERVİS') ||
      uEn.includes('SERVIS');
    const mlzmHint =
      uEn.includes('MLZM') ||
      uTr.includes('MALZEME') ||
      uEn.includes('MALZ') ||
      uTr.includes('STOK') ||
      uEn.includes('STOK');
    if (hzmtHint && !map.has('HZMT')) map.set('HZMT', key);
    if (mlzmHint && !map.has('MLZM')) map.set('MLZM', key);
  }
}

function buildRawLookupMaps(data: {
  birimler: Array<{ key: number; kod: string }>;
  kalemTurleri: Array<{ key: number; kod: string }>;
}): RawLineLookupMaps {
  const birimNormToKey = new Map<string, number>();
  for (const b of data.birimler ?? []) {
    const k = Number(b.key);
    if (!Number.isFinite(k) || k <= 0) continue;
    const norm = normalizeBirimLookupKey(String(b.kod ?? ''));
    if (norm) birimNormToKey.set(norm, k);
  }
  augmentBirimLookupMap(birimNormToKey);

  const kalemTuruUpperToKey = new Map<string, number>();
  for (const x of data.kalemTurleri ?? []) {
    const key = Number(x.key);
    if (!Number.isFinite(key) || key <= 0) continue;
    const ku = String(x.kod ?? '').trim().toUpperCase();
    if (ku) kalemTuruUpperToKey.set(ku, key);
  }
  augmentKalemTuruLookupMap(kalemTuruUpperToKey);
  return { birimNormToKey, kalemTuruUpperToKey };
}

async function ensureRawLineLookupMaps(targetFirmaKodu: number, targetDonemKodu: number): Promise<RawLineLookupMaps> {
  const ck = `${targetFirmaKodu}:${targetDonemKodu}`;
  if (rawLineLookupHttpCache?.cacheKey === ck) return rawLineLookupHttpCache.maps;
  const data = await InvoiceService.getRawLineLookups(targetFirmaKodu, targetDonemKodu);
  const maps = buildRawLookupMaps(data);
  rawLineLookupHttpCache = { cacheKey: ck, maps };
  return maps;
}

function inferRawKalemTuruCode(line: Record<string, unknown>): 'MLZM' | 'HZMT' {
  const ex = String(line.kalemTuru ?? '')
    .trim()
    .toUpperCase();
  if (ex === 'MLZM' || ex === 'HZMT') return ex as 'MLZM' | 'HZMT';
  const lblRaw = String(line.lineTypeLabel ?? '').trim();
  const lbl = lblRaw.toUpperCase();
  const lblTr = lblRaw.toLocaleUpperCase('tr-TR');
  if (lbl === 'STOK' || lbl === 'MLZM') return 'MLZM';
  if (lbl === 'HIZMET' || lbl === 'HZMT') return 'HZMT';
  if (lblTr.includes('HİZMET') || lbl.includes('HIZMET') || lbl.includes('HZM')) return 'HZMT';
  if (lblTr.includes('MALZEME') || lbl.includes('MALZ') || lbl.includes('MLZ') || lbl.includes('STOK')) return 'MLZM';
  const code = String(line.itemCode ?? line.stokKartKodu ?? '').trim();
  return code ? 'MLZM' : 'HZMT';
}

/** DİA / RPR döviz kodlarını satır–başlık karşılaştırması için tek forma indirger. */
function canonicalCurrencyCode(raw: string): string {
  const c = String(raw ?? '')
    .trim()
    .toUpperCase()
    .replace(/İ/g, 'I');
  if (!c) return '';
  if (c === 'TRY' || c === 'TL' || c === 'YTL' || c === '₺') return 'TL';
  if (c.startsWith('TL')) return 'TL';
  return c;
}

function resolveLineTargetDovizKey(
  line: Record<string, unknown>,
  header: Record<string, unknown>,
  lineDovizKeyByCode: Map<string, number>
): number | undefined {
  const preset = Number(line.targetKeySisDoviz);
  if (Number.isFinite(preset) && preset > 0) return preset;

  const hd = Number(header.targetSisDovizKey);
  const hCode = String(header.currencyCode ?? '').trim();
  const lcRaw = String(line.lineCurrencyCode ?? line.currencyCode ?? hCode).trim();
  const canonH = canonicalCurrencyCode(hCode);
  const canonL = canonicalCurrencyCode(lcRaw);

  if (canonL && canonH && canonL === canonH && Number.isFinite(hd) && hd > 0) return hd;

  const upper = lcRaw.toUpperCase();
  let k = lineDovizKeyByCode.get(upper);
  if (!(k != null && k > 0) && canonL === 'TL') {
    k = lineDovizKeyByCode.get('TRY') ?? lineDovizKeyByCode.get('TL');
  }
  if (k != null && k > 0) return k;

  if (Number.isFinite(hd) && hd > 0) return hd;
  return undefined;
}

function enrichSelectedLineSnapshotsForRaw(
  lines: unknown[] | undefined,
  header: Record<string, unknown>,
  maps: RawLineLookupMaps,
  lineDovizKeyByCode: Map<string, number>
): unknown[] {
  return (lines ?? []).map(raw => {
    const line = raw != null && typeof raw === 'object' ? { ...(raw as Record<string, unknown>) } : {};
    const kalemTuru = inferRawKalemTuruCode(line);
    const tKt = maps.kalemTuruUpperToKey.get(kalemTuru);
    const tKb = resolveBirimKeyFromRawMaps(maps.birimNormToKey, String(line.birimAdi ?? ''));
    const lineDv = resolveLineTargetDovizKey(line, header, lineDovizKeyByCode);
    return {
      ...line,
      kalemTuru,
      ...(tKt != null && tKt > 0 ? { targetKeyKalemTuru: tKt } : {}),
      ...(tKb != null && tKb > 0 ? { targetKeyKalemBirim: tKb } : {}),
      ...(lineDv != null && lineDv > 0 ? { targetKeySisDoviz: lineDv } : {}),
    };
  });
}

/** Backend TransferRawMode açıkken snapshot’a hedef cari/döviz _key ekler (dropdown gerektirmez). */
async function enrichPayloadsForRawMode(
  payloads: InvoiceTransferPayload[],
  transferRawMode: boolean,
  targetFirmaKodu: number,
  targetDonemKodu: number
): Promise<InvoiceTransferPayload[]> {
  const tf = Number(targetFirmaKodu);
  const td = Number(targetDonemKodu) || 0;
  if (!transferRawMode || tf <= 0 || td <= 0) return payloads;

  let lookupMaps: RawLineLookupMaps;
  try {
    lookupMaps = await ensureRawLineLookupMaps(tf, td);
  } catch (e) {
    const hint = e instanceof Error ? e.message : String(e);
    throw new Error(`RAW mode: birim/kalem türü listesi alınamadı (GET raw-line-lookups): ${hint}`);
  }

  const lineDovizCodesToResolve = new Set<string>();
  for (const p of payloads) {
    const hs = p.headerSnapshot;
    if (!hs || typeof hs !== 'object') continue;
    const hRec = hs as Record<string, unknown>;
    const hCode = String(hRec.currencyCode ?? '').trim();
    const canonH = canonicalCurrencyCode(hCode);
    for (const raw of p.selectedLineSnapshots ?? []) {
      if (raw == null || typeof raw !== 'object') continue;
      const l = raw as Record<string, unknown>;
      const lcRaw = String(l.lineCurrencyCode ?? l.currencyCode ?? hCode).trim();
      const canonL = canonicalCurrencyCode(lcRaw);
      if (lcRaw && canonL && canonH && canonL !== canonH) {
        lineDovizCodesToResolve.add(lcRaw);
        if (canonL === 'TL') {
          lineDovizCodesToResolve.add('TRY');
          lineDovizCodesToResolve.add('TL');
        }
      }
    }
  }

  const lineDovizKeyByCode = new Map<string, number>();
  await Promise.all(
    [...lineDovizCodesToResolve].map(async code => {
      try {
        const { key } = await InvoiceService.resolveTargetDovizKey(tf, td, code);
        if (key != null && key > 0) lineDovizKeyByCode.set(code.trim().toUpperCase(), key);
      } catch {
        /* Tek satır dövizi çözülemese bile başlık anahtarıyla denenecek */
      }
    })
  );

  return Promise.all(
    payloads.map(async p => {
      const hs = p.headerSnapshot;
      if (!hs || typeof hs !== 'object') {
        throw new Error(`RAW mode: headerSnapshot yok (fatura ${p.sourceInvoiceKey})`);
      }
      const rec = hs as Record<string, unknown>;
      const cariKod = String(rec.cariCode ?? '').trim();
      const cur = String(rec.currencyCode ?? '').trim();
      const extra: Record<string, unknown> = {};
      try {
        const [ck, dk] = await Promise.all([
          cariKod ? InvoiceService.resolveTargetCariKey(tf, td, cariKod) : Promise.resolve({ key: null as number | null }),
          cur ? InvoiceService.resolveTargetDovizKey(tf, td, cur) : Promise.resolve({ key: null as number | null }),
        ]);
        if (ck.key != null && ck.key > 0) extra.targetCariKey = ck.key;
        if (dk.key != null && dk.key > 0) {
          extra.targetSisDovizKey = dk.key;
          extra.targetSisDovizRaporlamaKey = dk.key;
        }
      } catch (e) {
        const hint = e instanceof Error ? e.message : String(e);
        throw new Error(`RAW mode: hedef lookup hatası (fatura ${p.sourceInvoiceKey}): ${hint}`);
      }

      const targetCariKey = Number(extra.targetCariKey);
      const targetSisDovizKey = Number(extra.targetSisDovizKey);
      if (
        !Number.isFinite(targetCariKey) ||
        targetCariKey <= 0 ||
        !Number.isFinite(targetSisDovizKey) ||
        targetSisDovizKey <= 0
      ) {
        throw new Error(
          `RAW mode: target keys resolve edilemedi (fatura ${p.sourceInvoiceKey}, cariKod=${cariKod || '—'}, döviz=${cur || '—'})`
        );
      }

      const headerMerged = { ...rec, ...extra } as Record<string, unknown>;
      const selectedLineSnapshots = enrichSelectedLineSnapshotsForRaw(
        p.selectedLineSnapshots,
        headerMerged,
        lookupMaps,
        lineDovizKeyByCode
      );
      const out = { ...p, headerSnapshot: headerMerged, selectedLineSnapshots };

      const lineIssues = collectRawLineSnapshotIssues(out.selectedLineSnapshots, headerMerged);
      if (lineIssues.length > 0) {
        console.error('RAW LINE SNAPSHOT INCOMPLETE', {
          sourceInvoiceKey: out.sourceInvoiceKey,
          issues: lineIssues,
          payload: out,
        });
        const summary = lineIssues
          .slice(0, 6)
          .map(x =>
            x.lineIndex < 0 ? `genel: ${x.reason}` : `satır ${x.lineIndex}: ${x.reason}`
          )
          .join('; ');
        const more = lineIssues.length > 6 ? ` …+${lineIssues.length - 6} ek` : '';
        throw new Error(
          `RAW mode: satır snapshot eksik (backend TryValidateRawSnapshot; fatura ${p.sourceInvoiceKey}): ${summary}${more}`
        );
      }

      return out;
    })
  );
}

type TransferTypeFilter = 'tum_faturalar' | 'dagitilacak_faturalar';
type UstIslemTuruFilter = '' | 'A' | 'B';

// ── Para formatı ──────────────────────────────────────────────────────────────
const fmt = (v: number, dvz = 'TRY') =>
  v.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) +
  (dvz === 'TRY' ? ' ₺' : ` ${dvz}`);

const fmtShort = (v: number) =>
  v.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

// Not: Real DİA'da kaynak şube listesi `sis_yetkili_firma_donem_sube_depo`
// üzerinden gelir (sube _key + ad). Burada sabit liste kullanmıyoruz.

// ─────────────────────────────────────────────────────────────────────────────
function App() {
  // Cache sürümü yükseltilirse kullanıcı tarafında eski cache otomatik bypass edilir.
  const RPR_CACHE_PREFIX = 'rpr_cache_v5';

  const hashSig = (s: string) => {
    // küçük, stabil hash (localStorage key uzunluğunu kontrol etmek için)
    let h = 5381;
    for (let i = 0; i < s.length; i++) h = ((h << 5) + h) ^ s.charCodeAt(i);
    return (h >>> 0).toString(16);
  };
  const retry = async <T,>(fn: () => Promise<T>, attempts = 3): Promise<T> => {
    let last: unknown;
    for (let i = 1; i <= attempts; i++) {
      try {
        return await fn();
      } catch (e) {
        last = e;
        if (i < attempts) await new Promise(r => setTimeout(r, 250 * i));
      }
    }
    throw last;
  };

  // Eski rapor cache'lerini temizle (firma/dönem karışıklığı olmasın).
  useEffect(() => {
    try {
      localStorage.removeItem('rpr_cache_v1');
      localStorage.removeItem('rpr_cache_v2');
      localStorage.removeItem('rpr_cache_v3');
    } catch {
      // ignore
    }
  }, []);

  // ── Havuz context lookups ────────────────────────────────────────────────
  const [companies, setCompanies] = useState<ISourceCompanyDto[]>([]);
  const [periods, setPeriods] = useState<ISourcePeriodDto[]>([]);
  const [branches, setBranches] = useState<ISourceBranchDto[]>([]);
  const [sourceDepots, setSourceDepots] = useState<ISourceDepotDto[]>([]);
  const [allBranchNameByKey, setAllBranchNameByKey] = useState<Record<number, string>>({});
  const [allDepotNameByKey, setAllDepotNameByKey] = useState<Record<number, string>>({});
  // fetchInvoices gibi callback'lerde stale state'e düşmemek için refs
  const allBranchNameByKeyRef = useRef<Record<number, string>>({});
  const allDepotNameByKeyRef = useRef<Record<number, string>>({});
  const [stokHizmetByFiyatKartKey] = useState<Record<string, { kodu: string; aciklama: string }>>({});
  const [unitByKey] = useState<Record<string, { kodu: string; adi: string }>>({});

  const [poolFirmaKodu, setPoolFirmaKodu] = useState<number>(0);
  const [poolFirmaAdi, setPoolFirmaAdi] = useState<string>('');
  const [defaultSourceDonemKodu, setDefaultSourceDonemKodu] = useState<number>(0);
  const [sourceDonemKodu, setSourceDonemKodu] = useState<number>(0);
  // KRİTİK: DİA RPR paramları numeric _key ister. 0 = "tümü".
  const [sourceSubeKey, setSourceSubeKey] = useState<number>(0); // sis_sube._key
  const [sourceDepoKey, setSourceDepoKey] = useState<number>(0); // sis_depo._key

  // ── Fatura verileri ───────────────────────────────────────────────────────
  const [, setInvoices]   = useState<IInvoiceListRow[]>([]);
  const [lines,      setLines]      = useState<IInvoiceLineListRow[]>([]);
  const [loading,    setLoading]    = useState(false);
  const [, setLinesLoading] = useState(false);

  // ── Filtre state ──────────────────────────────────────────────────────────
  const [filterFaturaNo, setFilterFaturaNo] = useState('');          // belgeno2 -> belgeno
  const [filterCari,    setFilterCari]    = useState('');            // cariunvan
  const [filterFaturaTuru, setFilterFaturaTuru] = useState('');      // turuack / turu_kisa
  const [filterDurum,   setFilterDurum]   = useState<'' | '0' | '1' | '2'>(''); // şimdilik client-side, 2. aşama
  const [filterKalemSube, setFilterKalemSube] = useState('');        // RPR: kalem_sube (dinamik_fatsube)
  const [filterBaslangic, setFilterBaslangic] = useState<string>(''); // YYYY-MM-DD
  const [filterBitis, setFilterBitis] = useState<string>('');         // YYYY-MM-DD
  // RPR param: fatura_tipi (WS'e gider) => SQL: 'TUM' | 'DAGIT'

  // RPR: WS sadece butonda; filtreler client-side çalışır
  const [bulkTransferSummary, setBulkTransferSummary] = useState<null | {
    ok: number;
    error: number;
    messages: string[];
  }>(null);

  // ── Seçim state ───────────────────────────────────────────────────────────
  const [activeInvoice, setActiveInvoice]   = useState<IInvoiceListRow | null>(null);
  const [selectedInvoiceKey, setSelectedInvoiceKey] = useState<number | null>(null); // scf_fatura._key — görüntülenen (radyo)
  /** Havuz toplu aktarım: seçilen kaynak faturalar (_key). Kalem seçimi buna göre türetilir. */
  const [selectedInvoiceKeys, setSelectedInvoiceKeys] = useState<Set<number>>(() => new Set());
  /** Dağıtılacak(Kalem) modunda satır bazlı seçim: kalem _key listesi */
  const [selectedLineKeysManual, setSelectedLineKeysManual] = useState<Set<string>>(() => new Set());

  /**
   * Tüm Faturalar modunda: fatura seçili olsa bile sadece istenen kalemleri seçebilmek için
   * invoiceKey -> lineKey[] override. Override varsa sadece bu kalemler aktarılır.
   */
  const [selectedLineKeysByInvoice, setSelectedLineKeysByInvoice] = useState<Record<string, string[]>>({});
  const [selectedKalemKeys, setSelectedKalemKeys] = useState<string[]>([]); // legacy UI state (devre dışı)

  const [activeTab, setActiveTab] = useState<'pool' | 'last'>('pool');
  // İlk açılış: tüm faturalar (fatura bazlı)
  const [transferTypeFilter, setTransferTypeFilter] = useState<TransferTypeFilter>('tum_faturalar');
  const [ustIslemTuruFilter, setUstIslemTuruFilter] = useState<UstIslemTuruFilter>('');

  type InvoiceSortKey =
    | ''
    | 'invoiceNo'
    | 'fisNo'
    | 'date'
    | 'invoiceType'
    | 'upperProcess'
    | 'cariName'
    | 'sourceBranch'
    | 'sourceDepot'
    | 'currency'
    | 'total'
    | 'discount'
    | 'expense'
    | 'vat'
    | 'net'
    | 'transferStatus'
    | 'dynamicBranch';

  const [invoiceSort, setInvoiceSort] = useState<{ key: InvoiceSortKey; dir: 'asc' | 'desc' }>({ key: '', dir: 'asc' });
  const toggleInvoiceSort = useCallback((key: Exclude<InvoiceSortKey, ''>) => {
    setInvoiceSort(prev => {
      if (prev.key !== key) return { key, dir: 'asc' };
      return { key, dir: prev.dir === 'asc' ? 'desc' : 'asc' };
    });
  }, []);
  const sortIcon = useCallback((key: Exclude<InvoiceSortKey, ''>) => {
    if (invoiceSort.key !== key) return '';
    return invoiceSort.dir === 'asc' ? ' ▲' : ' ▼';
  }, [invoiceSort.dir, invoiceSort.key]);

  // ── Hedef state — UYGULAMA ÖZEL ──────────────────────────────────────────
  const [targetFirmaKodu, setTargetFirmaKodu] = useState<number | ''>('');     // [ÖZEL] numeric target (firma_kodu)
  const [targetFirmaAdi,  setTargetFirmaAdi]  = useState('');                  // [ÖZEL] display
  const [targetSubeKodu,  setTargetSubeKodu]  = useState('');                  // [ÖZEL] subekodu (örn. "01")
  const [targetSubeAdi,   setTargetSubeAdi]   = useState('');                  // [ÖZEL] display
  const [targetSubeKey,   setTargetSubeKey]   = useState<number | ''>('');     // [ÖZEL] sis_sube._key (numeric)
  const [targetDonemKodu, setTargetDonemKodu] = useState<number | ''>('');     // [ÖZEL] numeric
  const [targetDonemKey,  setTargetDonemKey]  = useState<number | ''>('');     // [ÖZEL] sis_donem.key (numeric)
  const [targetDonemLabel, setTargetDonemLabel] = useState<string>('');
  const [targetDepoKey, setTargetDepoKey] = useState<number | ''>('');
  const [targetResolveInfo, setTargetResolveInfo] = useState<string>('');

  const [targetBranches, setTargetBranches] = useState<ISourceBranchDto[]>([]);
  const [targetPeriods, setTargetPeriods] = useState<ISourcePeriodDto[]>([]);
  const [targetDepots, setTargetDepots] = useState<ISourceDepotDto[]>([]);

  // ── RPR normalize edilmiş satırlar (TEK kaynak) ──────────────────────────
  const [allRows, setAllRows] = useState<NormalizedRprRow[]>([]);
  const [filteredRows, setFilteredRows] = useState<NormalizedRprRow[]>([]);

  /** Seçili faturaların tüm kalemlerinin lineKey kümesi (invalid dahil — seçim fatura bazlı). */
  const selectedLineKeys = useMemo(() => {
    if (transferTypeFilter === 'dagitilacak_faturalar') {
      return new Set(Array.from(selectedLineKeysManual).map(String).filter(x => x && x !== '0'));
    }
    const byInv = new Map<number, string[]>();
    for (const r of allRows) {
      const ik = Number(r.invoiceKey);
      if (!Number.isFinite(ik) || ik <= 0) continue;
      const lk = String(r.lineKey ?? '').trim();
      if (!lk || lk === '0') continue;
      const arr = byInv.get(ik) ?? [];
      arr.push(lk);
      byInv.set(ik, arr);
    }

    const s = new Set<string>();
    for (const ik of selectedInvoiceKeys) {
      const override = selectedLineKeysByInvoice[String(ik)];
      if (Array.isArray(override) && override.length > 0) {
        for (const lk of override) {
          const x = String(lk ?? '').trim();
          if (x && x !== '0') s.add(x);
        }
        continue;
      }
      const all = byInv.get(ik) ?? [];
      for (const lk of all) s.add(lk);
    }
    return s;
  }, [allRows, selectedInvoiceKeys, selectedLineKeysManual, transferTypeFilter, selectedLineKeysByInvoice]);

  const toggleLineKeyForInvoice = useCallback((invoiceKey: number, lineKey: string | number) => {
    const ik = Number(invoiceKey);
    const lk = String(lineKey ?? '').trim();
    if (!Number.isFinite(ik) || ik <= 0) return;
    if (!lk || lk === '0') return;

    setSelectedLineKeysByInvoice(prev => {
      const key = String(ik);
      const next = { ...prev };
      const cur = new Set<string>(Array.isArray(prev[key]) ? prev[key].map(String) : []);
      if (cur.has(lk)) cur.delete(lk);
      else cur.add(lk);
      const arr = Array.from(cur).filter(x => x && x !== '0');
      if (arr.length === 0) delete next[key];
      else next[key] = arr;
      return next;
    });

    // Satır seçimi varsa ilgili faturayı da seçili hale getir.
    setSelectedInvoiceKeys(prev => {
      const next = new Set(prev);
      if (next.has(ik)) {
        // Eğer bu satır kapatıldıysa ve invoice override boş kalacaksa, invoice'ı kaldırmak daha doğal.
        // Bu durumu yukarıdaki state async olduğu için burada kesin bilemeyiz; kullanıcı UX için manuel kaldırabilir.
        return next;
      }
      next.add(ik);
      return next;
    });
  }, []);

  const allLineRowsRef = useRef<NormalizedRprRow[]>([]);
  const filteredLineRowsRef = useRef<NormalizedRprRow[]>([]);

  // ── Log state ─────────────────────────────────────────────────────────────
  const [logs,         setLogs]         = useState<ITransferLogEntry[]>([]);
  const [showLogPanel, setShowLogPanel] = useState(false);
  const [transferring, setTransferring] = useState(false);
  const [transferFlags, setTransferFlags] = useState({
    transferRawMode: false,
    transferConcurrency: 0,
    transferBatchSize: 0,
  });
  const [transferProgress, setTransferProgress] = useState<{ total: number; success: number; failed: number; remaining: number; inFlight: number } | null>(null);
  const [failedInvoices, setFailedInvoices] = useState<Array<{ sourceInvoiceKey: number; selectedKalemKeys: number[] }>>([]);
  const [transferLogs, setTransferLogs] = useState<Array<{
    invoiceKey: number;
    lineCount: number;
    targetFirma: number;
    success: boolean;
    errorMessage?: string;
    timestamp: string;
  }>>(() => {
    try {
      const raw = localStorage.getItem('transfer_logs_v1');
      const arr = raw ? JSON.parse(raw) : [];
      return Array.isArray(arr) ? arr : [];
    } catch {
      return [];
    }
  });

  const [showTransferLogPanel, setShowTransferLogPanel] = useState(false);

  // Backend'de kalıcı (App_Data/transfer_logs.jsonl) transfer loglarını yükle ve local ile birleştir.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await InvoiceService.getRecentTransferLogs(800);
        const items = Array.isArray((res as any)?.items) ? (res as any).items : [];
        if (cancelled || items.length === 0) return;
        setTransferLogs(prev => {
          const key = (x: any) =>
            `${Number(x.invoiceKey) || 0}|${Number(x.lineCount) || 0}|${Number(x.targetFirma) || 0}|${x.success ? 1 : 0}|${String(x.timestamp || '')}|${String(x.errorMessage || '')}`;
          const seen = new Set(prev.map(key));
          const merged = [...prev];
          for (const it of items) {
            const k = key(it);
            if (seen.has(k)) continue;
            seen.add(k);
            merged.push(it);
          }
          // timestamp sırasına göre tut
          merged.sort((a, b) => String(a.timestamp).localeCompare(String(b.timestamp)));
          try { localStorage.setItem('transfer_logs_v1', JSON.stringify(merged.slice(-2000))); } catch {}
          return merged.slice(-2000);
        });
      } catch {
        /* ignore */
      }
    })();
    return () => { cancelled = true; };
  }, []);

  const persistTransferLogs = useCallback((next: any[]) => {
    try { localStorage.setItem('transfer_logs_v1', JSON.stringify(next.slice(-2000))); } catch {}
  }, []);
  const pushTransferLogItem = useCallback((item: { invoiceKey: number; lineCount: number; targetFirma: number; success: boolean; errorMessage?: string }) => {
    const entry = { ...item, timestamp: new Date().toISOString() };
    setTransferLogs(prev => {
      const next = [...prev, entry];
      persistTransferLogs(next);
      // fire-and-forget backend write (best effort)
      void InvoiceService.transferLog([entry]).catch(() => {});
      return next;
    });
  }, [persistTransferLogs]);
  const abortRef = useRef<AbortController | null>(null);
  const cancelRequestedRef = useRef(false);
  const [lookupError, setLookupError] = useState<string | null>(null);
  const [transferAlert, setTransferAlert] = useState<string | null>(null);
  const [lastTransfers, setLastTransfers] = useState<Array<{
    createdInvoiceKey: number;
    targetFirmaKodu: number;
    targetFirmaAdi: string;
    targetDonemKodu: number;
    targetSubeKey: number;
    targetDepoKey: number;
    targetFisNo?: string;
    targetCariKod?: string;
    targetCariUnvan?: string;
    /** Havuz / snapshot (liste anında dolar; KEY# yerine gösterilir). */
    poolInvoiceNo?: string;
    poolFisNo?: string;
    poolDate?: string;
    poolCariKod?: string;
    poolCariUnvan?: string;
    poolNet?: number;
    /** Havuz (kaynak) fatura invoiceKey — alt satır açıklaması. */
    poolSourceInvoiceKey?: number;
    /** Dağıtılacak (Kalem) modunda kaynak satır key */
    poolSourceLineKey?: number;
    /** Kaynak filtreleri için */
    poolSourceSubeKey?: number;
    poolSourceDepoKey?: number;
    /** Kalem detayı (Son Aktarılan > fatura açılımında gösterilir) */
    lineItemCode?: string;
    lineItemName?: string;
    lineUnitName?: string;
    lineQuantity?: number;
    lineTotal?: number;
    poolUpperProcess?: string;
    poolKaynakSube?: string;
    poolKaynakDepo?: string;
    poolDoviz?: string;
    poolToplam?: number;
    poolIndirim?: number;
    poolMasraf?: number;
    poolKdv?: number;
    poolVeri?: string;
    poolTransferStatus?: string;
    poolKalemSube?: string;
  }>>([]);

  const [expandedLastInvoiceKeys, setExpandedLastInvoiceKeys] = useState<Set<number>>(() => new Set());
  const toggleExpandedLastInvoiceKey = useCallback((invKey: number) => {
    if (!Number.isFinite(invKey) || invKey <= 0) return;
    setExpandedLastInvoiceKeys(prev => {
      // UX: Son Aktarılan’da aynı anda tek fatura açık kalsın (ekran şişmesin).
      if (prev.has(invKey)) return new Set();           // tekrar tıklayınca kapat
      return new Set([invKey]);                         // başka faturaya tıklayınca eskisini kapat
    });
  }, []);

  // Son Aktarılan (fallback): eski transferlerde lastTransfers boş olabilir; transfer_logs_v1'dan havuz metasıyla tamamla.
  const derivedLastTransfersFromLogs = useMemo(() => {
    if (lastTransfers.length > 0) return [];
    const ok = (transferLogs || []).filter(x => x?.success === true && Number(x.invoiceKey) > 0);
    if (ok.length === 0) return [];
    const sorted = [...ok].sort((a, b) => String(b.timestamp).localeCompare(String(a.timestamp)));
    const used = new Set<string>();
    const out: any[] = [];
    for (const e of sorted) {
      const invK = Number(e.invoiceKey);
      const tag = `${invK}`;
      if (used.has(tag)) continue;
      used.add(tag);
      const { meta, cariRow } = pickInvoiceRowsForTransferDisplay(allRows, invK);
      if (!meta) continue;
      const hs: Record<string, unknown> | undefined = undefined;
      const poolInvoiceNo = String(meta.invoiceNo ?? '').trim();
      const poolFisNo = String(meta.fisNo ?? '').trim();
      const poolDate = String(meta.date ?? '').trim();
      const poolCariKod = pickPoolCariKodFromTransfer(hs, cariRow);
      const poolCariUnvan = String(cariRow?.cariName ?? meta.cariName ?? '').trim();
      const poolNetVal = pickPoolNetForLastTransfer(hs, meta);
      const kaynakSubeResolved =
        String(allBranchNameByKeyRef.current?.[Number(meta.sourceBranchKey)] ?? '').trim() ||
        String(meta.sourceBranchName ?? '').trim();
      const kaynakDepoResolved =
        String(allDepotNameByKeyRef.current?.[Number(meta.sourceWarehouseKey)] ?? '').trim() ||
        String(meta.sourceWarehouseName ?? '').trim();
      out.push({
        createdInvoiceKey: 0,
        targetFirmaKodu: Number(e.targetFirma) || 0,
        targetFirmaAdi: '',
        targetDonemKodu: 0,
        targetSubeKey: 0,
        targetDepoKey: 0,
        ...(poolInvoiceNo ? { poolInvoiceNo } : {}),
        ...(poolFisNo ? { poolFisNo } : {}),
        ...(poolDate ? { poolDate } : {}),
        ...(poolCariKod ? { poolCariKod } : {}),
        ...(poolCariUnvan ? { poolCariUnvan } : {}),
        ...(poolNetVal != null && Number.isFinite(poolNetVal) ? { poolNet: poolNetVal } : {}),
        poolSourceInvoiceKey: invK,
        ...poolMetaToLastTransferGrid(meta),
        ...(kaynakSubeResolved ? { poolKaynakSube: kaynakSubeResolved } : {}),
        ...(kaynakDepoResolved ? { poolKaynakDepo: kaynakDepoResolved } : {}),
        poolTransferStatus: 'Aktarıldı',
      });
      if (out.length >= 50) break;
    }
    return out;
  }, [allRows, lastTransfers.length, transferLogs]);

  // ── Son aktarılan kalıcı kayıt (localStorage) ────────────────────────────
  const LAST_TRANSFERS_KEY = 'last_transfers_v1';
  const [lastTransfersHydrated, setLastTransfersHydrated] = useState(false);
  useEffect(() => {
    try {
      const raw = localStorage.getItem(LAST_TRANSFERS_KEY);
      if (!raw) return;
      const arr = JSON.parse(raw);
      if (!Array.isArray(arr)) return;
      // hafif validasyon + limit
      const cleaned = arr
        .filter(x => x && typeof x === 'object')
        .slice(-50);
      if (cleaned.length > 0) setLastTransfers(cleaned as any);
    } catch {
      /* ignore */
    } finally {
      // İlk render'da "[]" yazıp eski kayıtları silmemek için hydrate bayrağı.
      setLastTransfersHydrated(true);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!lastTransfersHydrated) return;
    try {
      localStorage.setItem(LAST_TRANSFERS_KEY, JSON.stringify(lastTransfers.slice(-50)));
    } catch {
      /* ignore */
    }
  }, [lastTransfers]);

  // Son Aktarılan satırları eskiyse (Kaynak Şube/Depo boş), havuz meta satırından tamamla.
  useEffect(() => {
    if (lastTransfers.length === 0) return;
    if (allRows.length === 0) return;
    let changed = false;
    const next = lastTransfers.map(t => {
      if (t.poolKaynakSube && t.poolKaynakDepo) return t;
      const invK = Number(t.poolSourceInvoiceKey);
      if (!Number.isFinite(invK) || invK <= 0) return t;
      const { meta } = pickInvoiceRowsForTransferDisplay(allRows, invK);
      if (!meta) return t;
      const sube =
        String(allBranchNameByKeyRef.current?.[Number(meta.sourceBranchKey)] ?? '').trim() ||
        String(meta.sourceBranchName ?? '').trim();
      const depo =
        String(allDepotNameByKeyRef.current?.[Number(meta.sourceWarehouseKey)] ?? '').trim() ||
        String(meta.sourceWarehouseName ?? '').trim();
      if (!sube && !depo) return t;
      changed = true;
      return {
        ...t,
        ...(sube ? { poolKaynakSube: sube } : {}),
        ...(depo ? { poolKaynakDepo: depo } : {}),
      };
    });
    if (changed) setLastTransfers(next);
  }, [allRows, lastTransfers]);

  // ── Local duplicate registry (fingerprint) ───────────────────────────────
  const TRANSFER_REGISTRY_KEY = 'transfer_registry_v1';
  const transferRegistryRef = useRef<Set<string>>(new Set());
  // invoiceKey:lineKey -> transferred at least once (any target)
  const transferredLineIndexRef = useRef<Set<string>>(new Set());
  const didBackfillLastTransfersFromRegistryRef = useRef(false);
  useEffect(() => {
    try {
      const raw = localStorage.getItem(TRANSFER_REGISTRY_KEY);
      const arr = raw ? (JSON.parse(raw) as any[]) : [];
      if (Array.isArray(arr)) {
        transferRegistryRef.current = new Set(arr.map(x => String(x)));
        const idx = new Set<string>();
        for (const k of transferRegistryRef.current) {
          const parts = String(k).split(':');
          if (parts.length < 2) continue;
          const a = Number(parts[0]);
          const b = Number(parts[1]);
          if (!Number.isFinite(a) || a <= 0 || !Number.isFinite(b) || b <= 0) continue;
          idx.add(`${a}:${b}`);
        }
        transferredLineIndexRef.current = idx;
      }
    } catch {}
  }, []);

  // “Son Aktarılan” backfill: eski aktarım geçmişi registry’de var ama lastTransfers boş kalmış olabilir.
  // RPR satırları geldikten sonra, dağıtılacak (kalem) geçmişini Son Aktarılan’a yazar ve kalıcı hale getirir.
  useEffect(() => {
    if (didBackfillLastTransfersFromRegistryRef.current) return;
    if (!allRows || allRows.length === 0) return;
    try {
      const raw = localStorage.getItem(TRANSFER_REGISTRY_KEY);
      const arr = raw ? (JSON.parse(raw) as any[]) : [];
      if (!Array.isArray(arr) || arr.length === 0) return;

      // Sadece invoiceKey:lineKey bazında benzersizle.
      const pairs = new Set<string>();
      for (const k of arr) {
        const parts = String(k || '').split(':');
        if (parts.length < 2) continue;
        const invK = Number(parts[0]);
        const lk = Number(parts[1]);
        if (!Number.isFinite(invK) || invK <= 0 || !Number.isFinite(lk) || lk <= 0) continue;
        pairs.add(`${invK}:${lk}`);
      }
      if (pairs.size === 0) return;

      setLastTransfers(prev => {
        const existing = new Set<string>();
        for (const t of prev) {
          const invK = Number(t.poolSourceInvoiceKey);
          const lk = Number(t.poolSourceLineKey);
          if (Number.isFinite(invK) && invK > 0 && Number.isFinite(lk) && lk > 0) existing.add(`${invK}:${lk}`);
        }

        const newEntries: any[] = [];
        for (const tag of pairs) {
          if (existing.has(tag)) continue;
          const [a, b] = tag.split(':').map(Number);
          const row =
            allRows.find(r => Number(r.invoiceKey) === a && Number(r.lineKey) === b && !r.invalid)
            ?? allRows.find(r => Number(r.invoiceKey) === a && Number(r.lineKey) === b);
          if (!row) continue;

          // Dağıtılacak geçmişi: dinamik şube (kalem şube) olan satırlar.
          const dyn = String(row.dynamicBranch ?? '').trim();
          if (!dyn) continue;

          const kaynakSubeResolved =
            String(allBranchNameByKeyRef.current?.[Number(row.sourceBranchKey)] ?? '').trim() ||
            String(row.sourceBranchName ?? '').trim();
          const kaynakDepoResolved =
            String(allDepotNameByKeyRef.current?.[Number(row.sourceWarehouseKey)] ?? '').trim() ||
            String(row.sourceWarehouseName ?? '').trim();

          const entry = {
            createdInvoiceKey: 0,
            targetFirmaKodu: 0,
            targetFirmaAdi: '',
            targetDonemKodu: 0,
            targetSubeKey: 0,
            targetDepoKey: 0,
            poolInvoiceNo: String(row.invoiceNo ?? '').trim(),
            poolFisNo: String(row.fisNo ?? '').trim(),
            poolDate: String(row.date ?? '').trim(),
            poolCariKod: String(row.cariCode ?? '').trim(),
            poolCariUnvan: String(row.cariName ?? '').trim(),
            poolNet: Number.isFinite(Number(row.invoiceNet)) ? Number(row.invoiceNet) : undefined,
            poolSourceInvoiceKey: a,
            poolSourceLineKey: b,
            poolSourceSubeKey: Number(row.sourceBranchKey) || 0,
            poolSourceDepoKey: Number(row.sourceWarehouseKey) || 0,
            ...(row.itemCode ? { lineItemCode: String(row.itemCode) } : {}),
            ...(row.itemName ? { lineItemName: String(row.itemName) } : {}),
            ...(row.unitName ? { lineUnitName: String(row.unitName) } : {}),
            ...(row.quantity != null ? { lineQuantity: Number(row.quantity) } : {}),
            ...(row.lineTotal != null ? { lineTotal: Number(row.lineTotal) } : {}),
            ...poolMetaToLastTransferGrid(row),
            ...(kaynakSubeResolved ? { poolKaynakSube: kaynakSubeResolved } : {}),
            ...(kaynakDepoResolved ? { poolKaynakDepo: kaynakDepoResolved } : {}),
            poolTransferStatus: 'Aktarıldı',
            poolKalemSube: dyn,
          };
          newEntries.push(entry);
        }

        if (newEntries.length === 0) return prev;

        // Sıralama: en yeni tarih üstte gibi görünmesi için sona ekleyip tablo akışında en altta kalır.
        // Kullanıcı “birikiyor” beklentisi için bu yeterli; limit 50.
        const next = [...prev, ...newEntries].slice(-50);
        try { localStorage.setItem(LAST_TRANSFERS_KEY, JSON.stringify(next.slice(-50))); } catch {}
        return next;
      });

      didBackfillLastTransfersFromRegistryRef.current = true;
    } catch {
      // ignore
    }
  }, [allRows]);

  useEffect(() => {
    let alive = true;
    InvoiceService.getTransferFlags()
      .then(f => {
        if (alive)
          setTransferFlags({
            transferRawMode: Boolean(f.transferRawMode),
            transferConcurrency: f.transferConcurrency ?? 0,
            transferBatchSize: f.transferBatchSize ?? 0,
          });
      })
      .catch(() => {});
    return () => {
      alive = false;
    };
  }, []);

  const persistTransferRegistry = useCallback(() => {
    try {
      localStorage.setItem(TRANSFER_REGISTRY_KEY, JSON.stringify(Array.from(transferRegistryRef.current)));
    } catch {}
    try {
      const idx = new Set<string>();
      for (const k of transferRegistryRef.current) {
        const parts = String(k).split(':');
        if (parts.length < 2) continue;
        const a = Number(parts[0]);
        const b = Number(parts[1]);
        if (!Number.isFinite(a) || a <= 0 || !Number.isFinite(b) || b <= 0) continue;
        idx.add(`${a}:${b}`);
      }
      transferredLineIndexRef.current = idx;
    } catch {
      /* ignore */
    }
  }, []);

  const resetSelectionState = useCallback((clearInvoices = false) => {
    setActiveInvoice(null);
    setSelectedInvoiceKey(null);
    setSelectedInvoiceKeys(new Set());
    setSelectedLineKeysManual(new Set());
    setSelectedKalemKeys([]);
    setLines([]);
    // Kritik: kaynak liste/filtre değişince hedef aktarım paneli state'i taşınmasın.
    setTargetFirmaKodu('');
    setTargetFirmaAdi('');
    setTargetSubeKodu('');
    setTargetSubeKey('');
    setTargetSubeAdi('');
    setTargetDepoKey('');
    setTargetDonemKodu('');
    setTargetDonemKey('');
    setTargetDonemLabel('');
    setTargetResolveInfo('');
    setTargetBranches([]);
    setTargetPeriods([]);
    setTargetDepots([]);
    setAutoTargetLockError(null);
    setTransferAlert(null);
    // UX: filtre/şube/dönem değişiminde tabloyu "boş" yapma; loading overlay ile yenisini getir.
    // Sadece manuel refresh gibi durumlarda clearInvoices kullan.
    if (clearInvoices) setInvoices([]);
  }, []);

  const clearLastTransferState = useCallback(() => {
    setLastTransfers([]);
    try { localStorage.removeItem(LAST_TRANSFERS_KEY); } catch {}
  }, []);

  const ensureCompaniesLoaded = useCallback(async (forceRefresh = false) => {
    if (!forceRefresh && companies.length > 0) return;
    try {
      const comps = await retry(() => InvoiceService.getCompanies(forceRefresh), 2);
      // Boş yanıt veya geçici API hata cevabı mevcut listeyi silmesin (17 -> 0 flicker).
      setCompanies(prev => (comps.length > 0 ? comps : prev));
      if (!poolFirmaKodu) {
        const pool = await retry(() => InvoiceService.getPool(), 2);
        setPoolFirmaKodu(pool.poolFirmaKodu);
        setPoolFirmaAdi(pool.poolFirmaAdi ?? '');
      }
    } catch (e) {
      console.warn('[EnsureCompanies] reload failed', e);
    }
  }, [companies.length, poolFirmaKodu]);

  // ── İlk yükleme — şirket/dönem/şube lookups ─────────────────────────────
  useEffect(() => {
    const init = async () => {
      let defaults: { defaultSourceFirmaKodu?: number; defaultSourceDonemKodu?: number; defaultSourceSubeKey?: number } = {};
      try {
        const d = await InvoiceService.getDefaultSource();
        defaults = {
          defaultSourceFirmaKodu: d.defaultSourceFirmaKodu,
          defaultSourceDonemKodu: d.defaultSourceDonemKodu,
          defaultSourceSubeKey: d.defaultSourceSubeKey
        };
      } catch (err) {
        console.warn('[Init] default-source failed, using lookup defaults', err);
      }

      let selectedDonem = Number(defaults?.defaultSourceDonemKodu ?? 0);
      setDefaultSourceDonemKodu(selectedDonem > 0 ? selectedDonem : 0);
      // Otomatik şube seçme yok (yanlış şube -> boş liste). Kullanıcı manuel seçecek.
      let selectedSube = 0;
      let selectedPoolFirmaKodu = 0;
      let selectedPoolFirmaAdi = '';

      let comps: ISourceCompanyDto[] = [];

      try {
        comps = await retry(() => InvoiceService.getCompanies());
        setCompanies(prev => (comps.length > 0 ? comps : prev));
        setLookupError(null);
      } catch (err) {
        console.error('[Init] companies failed', err);
        setLookupError('Firma lookup başarısız');
        pushLog({ source_kalem_key: '', status: 'error', message: 'Firma listesi alınamadı. Lütfen Yenile butonuna basın.', was_duplicate_override: false });
      }

      // Havuz firma sabit: önce /pool dene, olmazsa company listeden fallback yap
      try {
        const pool = await retry(() => InvoiceService.getPool());
        selectedPoolFirmaKodu = pool.poolFirmaKodu;
        selectedPoolFirmaAdi = pool.poolFirmaAdi ?? '';
      } catch (err) {
        console.warn('[Init] pool failed, fallback to companies', err);
        const defaultSourceFirmaKodu = Number(defaults?.defaultSourceFirmaKodu ?? 0);
        const fallbackPool =
          (defaultSourceFirmaKodu > 0 ? comps.find(c => c.firma_kodu === defaultSourceFirmaKodu) : undefined)
          ?? comps.find(c => (c.firma_adi ?? '').toUpperCase().includes('TEST'))
          ?? comps.find(c => c.firma_kodu === 4)
          ?? comps.find(c => c.firma_kodu === 1)
          ?? comps[0];
        if (fallbackPool) {
          selectedPoolFirmaKodu = fallbackPool.firma_kodu;
          selectedPoolFirmaAdi = fallbackPool.firma_adi ?? '';
        }
        setLookupError('Havuz firma bilgisi alınamadı; firma listesinden fallback kullanıldı.');
      }

      // API boş döndüyse bile liste isteği atılabilsin (default-source / TEST ile uyumlu).
      const hardFirma =
        selectedPoolFirmaKodu > 0
          ? selectedPoolFirmaKodu
          : (Number(defaults?.defaultSourceFirmaKodu) > 0 ? Number(defaults.defaultSourceFirmaKodu) : 4);
      if (selectedPoolFirmaKodu <= 0 && hardFirma > 0) {
        selectedPoolFirmaKodu = hardFirma;
        selectedPoolFirmaAdi =
          comps.find(c => c.firma_kodu === hardFirma)?.firma_adi?.trim()
          || 'TEST FİRMA';
        setLookupError(
          'Havuz firma API’den gelmedi; varsayılan havuz kullanılıyor. Backend (5189) çalışıyorsa Yenile’ye basın.'
        );
      }

      setPoolFirmaKodu(selectedPoolFirmaKodu);
      setPoolFirmaAdi(selectedPoolFirmaAdi);

      const hardDonem =
        Number(defaults?.defaultSourceDonemKodu) > 0 ? Number(defaults.defaultSourceDonemKodu) : 3;

      try {
        if (!selectedPoolFirmaKodu) throw new Error('poolFirmaKodu missing');
        const ps = await retry(() => InvoiceService.getPeriods(selectedPoolFirmaKodu));
        setPeriods(ps);
        // Dönem: config → öntanımlı → en yüksek kod; liste boşsa hardDonem ile devam
        if (ps.length === 0) {
          setSourceDonemKodu(hardDonem);
        } else {
          const preferred =
            (selectedDonem > 0 ? ps.find(x => x.donemkodu === selectedDonem) : undefined)
            ?? (hardDonem > 0 ? ps.find(x => x.donemkodu === hardDonem) : undefined)
            ?? ps.find(x => x.ontanimli)
            ?? [...ps].sort((a, b) => b.donemkodu - a.donemkodu)[0];
          if (preferred) {
            selectedDonem = preferred.donemkodu;
            setSourceDonemKodu(preferred.donemkodu);
          } else {
            setSourceDonemKodu(hardDonem);
          }
        }
      } catch (err) {
        console.error('[Init] periods failed', err);
        setPeriods([]);
        setSourceDonemKodu(hardDonem);
        setLookupError('Dönem listesi alınamadı; varsayılan dönem kodu ile denenecek.');
      }

      try {
        if (!selectedPoolFirmaKodu) throw new Error('poolFirmaKodu missing');
        const bs = await retry(() => InvoiceService.getBranches(selectedPoolFirmaKodu, selectedDonem));
        setBranches(bs);
        if (selectedSube > 0 && bs.some(x => x.key === selectedSube)) {
          setSourceSubeKey(selectedSube);
        } else {
          // Otomatik şube seçme: varsayılan "Tüm Şubeler"
          selectedSube = 0;
          setSourceSubeKey(0);
        }
      } catch (err) {
        console.error('[Init] branches failed', err);
        setBranches([]);
      }

      try {
        // Depo şubeye bağlı; "Tüm Şubeler" seçiliyken depo disabled olmalı.
        if (!selectedPoolFirmaKodu || selectedSube <= 0) {
          setSourceDepots([]);
          setSourceDepoKey(0);
        } else {
          const deps = await retry(() => InvoiceService.getDepots(selectedPoolFirmaKodu, selectedSube, selectedDonem));
          setSourceDepots(deps);
          setSourceDepoKey(0);
        }
      } catch (err) {
        console.error('[Init] depots failed', err);
        setSourceDepots([]);
        setSourceDepoKey(0);
      }
    };
    init();
  }, []); // eslint-disable-line



  // ── Fatura listesi yükle ──────────────────────────────────────────────────
  const invoiceListReqId = useRef(0);
  const lastReportFetchSig = useRef<string>('');
  const fetchInvoices = useCallback(async (forceFresh = false) => {
    // Double click / spam engeli
    if (loading) return;
    const myReqId = ++invoiceListReqId.current;
    if (!poolFirmaKodu || !sourceDonemKodu) {
      setInvoices([]);
      setAllRows([]);
      setFilteredRows([]);
      setLoading(false);
      setTransferAlert(
        'Kaynak havuz veya dönem yüklenemedi. Backend (5189) açık mı kontrol edin; ardından Yenile’ye basın.'
      );
      return;
    }
    setLoading(true);
    try {
      setTransferAlert(null);
      // Tek veri kaynağı: RPR000000004 (rpr_raporsonuc_getir)
      // NOT: Üst filtreler (cari_adi, fatura_no, kalem_sube vs.) WS'e gitmez.
      // Sadece burada 1 kez veri çekilir; filtreleme `allRows` üzerinde client-side çalışır.

      // Seçili kaynak dönemin tarih aralığında çek (tüm dönemlerin min–max değil → gereksiz yıl karışması önlenir).
      const sortedBas = [...periods]
        .map(p => String(p.baslangic_tarihi ?? '').trim().slice(0, 10))
        .filter(Boolean)
        .sort();
      const sortedBit = [...periods]
        .map(p => String(p.bitis_tarihi ?? '').trim().slice(0, 10))
        .filter(Boolean)
        .sort();
      const basFallback = sortedBas[0] || '2000-01-01';
      const bitFallback = sortedBit[sortedBit.length - 1] || '2099-12-31';
      const pSel = periods.find(x => Number(x.donemkodu) === Number(sourceDonemKodu));
      const baslangic =
        (pSel?.baslangic_tarihi ? String(pSel.baslangic_tarihi).trim().slice(0, 10) : '') || basFallback;
      const bitis = (pSel?.bitis_tarihi ? String(pSel.bitis_tarihi).trim().slice(0, 10) : '') || bitFallback;
      const effectiveDonem = Number(sourceDonemKodu) || 1;

      const basePayload = {
        firma_kodu: poolFirmaKodu || undefined,
        donem_kodu: effectiveDonem || undefined,
        report_code: 'RPR000000004',
        baslangic,
        bitis,
        // WS'e üst filtre gönderme: her şey client-side filtrelenir.
        kaynak_sube: 0,
        kaynak_depo: 0,
        ust_islem: 'TUM',
        cari_adi: '',
        fatura_no: '',
        fatura_turu: '',
        kalem_sube: '',
      };

      if (invoiceListReqId.current !== myReqId) return; // stale
      // Cache signature: kullanıcı tarih/şube/depo değiştirdiğinde eski cache kullanılmasın.
      // Not: filtreler WS tetiklemez; sadece "Verileri Çek" basılınca cache hit/miss etkiler.
      const sig = JSON.stringify({
        ...basePayload,
        filterBaslangic: (filterBaslangic ?? '').toString().trim(),
        filterBitis: (filterBitis ?? '').toString().trim(),
        kaynak_sube: Number(sourceSubeKey) || 0,
        kaynak_depo: Number(sourceDepoKey) || 0,
      });
      const cacheKey = `${RPR_CACHE_PREFIX}_${hashSig(sig)}`;

      // Eski cache'i otomatik temizle: backend/rapor mapping fixleri sonrası kullanıcı manual "localStorage.clear" yapmak zorunda kalmasın.
      // Sadece aynı sig için üretilen cacheKey tutulur; diğer tüm rpr_cache_* anahtarları silinir.
      try {
        for (let i = localStorage.length - 1; i >= 0; i--) {
          const k = localStorage.key(i) ?? '';
          if (!k) continue;
          if (k.startsWith('rpr_cache_') && k !== cacheKey) localStorage.removeItem(k);
        }
      } catch {}
      // Persisted cache: forceFresh değilse sayfa yenilense bile WS'e gitme.
      try {
        const cachedRaw = !forceFresh ? localStorage.getItem(cacheKey) : null;
        if (cachedRaw) {
          const cached = JSON.parse(cachedRaw);
          if (cached?.sig === sig && Array.isArray(cached?.rows)) {
            if (debugEnabled) {
              // eslint-disable-next-line no-console
              console.debug('[rpr] cache hit', { donem: effectiveDonem, baslangic, bitis });
            }
            const normalized = (cached.rows as any[]).map((r: any) =>
              normalizeRprRow(r, {
                currencyFallbackCode: '',
                resolveBranchName: (k: number) => String(allBranchNameByKey?.[Number(k)] ?? '').trim(),
                resolveWarehouseName: (k: number) => String(allDepotNameByKey?.[Number(k)] ?? '').trim(),
                targetFirmKey: targetFirmaKodu,
                targetBranchKey: targetSubeKey,
                targetWarehouseKey: targetDepoKey,
                targetPeriodKey: targetDonemKey,
              })
            );

            // Cache hit olsa bile eksik şube/depo isimlerini resolve etmeyi dene (aksi halde "Bilinmiyor" takılı kalır).
            try {
              const uniqSube = new Set<number>();
              const uniqDepo = new Set<number>();
              for (const r of normalized) {
                const bk = Number(r.sourceBranchKey);
                const dk = Number(r.sourceWarehouseKey);
                if (Number.isFinite(bk) && bk > 0) uniqSube.add(bk);
                if (Number.isFinite(dk) && dk > 0) uniqDepo.add(dk);
              }
              const subeKeys = Array.from(uniqSube);
              const depoKeys = Array.from(uniqDepo);
              const missingSube = subeKeys.filter(k => !String(allBranchNameByKey?.[k] ?? '').trim());
              const missingDepo = depoKeys.filter(k => !String(allDepotNameByKey?.[k] ?? '').trim());
              if ((missingSube.length > 0 || missingDepo.length > 0) && poolFirmaKodu && effectiveDonem) {
                pushLog({
                  source_kalem_key: '',
                  status: 'success',
                  message: `Şube/depo resolve (cache): missingSube=${missingSube.length} missingDepo=${missingDepo.length}`,
                  was_duplicate_override: false,
                });
                const resolved = await InvoiceService.resolveSubeDepoNames({
                  firmaKodu: poolFirmaKodu,
                  donemKodu: effectiveDonem,
                  subeKeys: missingSube.slice(0, 500),
                  depoKeys: missingDepo.slice(0, 500),
                });
                if (resolved?.sube && typeof resolved.sube === 'object') {
                  setAllBranchNameByKey(prev => {
                    const next = { ...(prev ?? {}) } as Record<number, string>;
                    for (const [k, v] of Object.entries(resolved.sube)) {
                      const nk = Number(k);
                      const nv = String(v ?? '').trim();
                      // resolve endpoint tenant doğrusudur: branches-all'daki eksik/yanlış ismi ez.
                      if (nk > 0 && nv) next[nk] = nv;
                    }
                    return next;
                  });
                }
                if (resolved?.depo && typeof resolved.depo === 'object') {
                  setAllDepotNameByKey(prev => {
                    const next = { ...(prev ?? {}) } as Record<number, string>;
                    for (const [k, v] of Object.entries(resolved.depo)) {
                      const nk = Number(k);
                      const nv = String(v ?? '').trim();
                      if (nk > 0 && nv) next[nk] = nv;
                    }
                    return next;
                  });
                }
              }
            } catch (e: any) {
              pushLog({
                source_kalem_key: '',
                status: 'error',
                message: `Şube/depo resolve (cache) hata: ${e?.message ?? 'bilinmiyor'}`,
                was_duplicate_override: false,
              });
            }
            setAllRows(normalized);
            setFilteredRows(normalized);
            allLineRowsRef.current = normalized;
            filteredLineRowsRef.current = normalized;
            lastReportFetchSig.current = sig;
            setLoading(false);
            return;
          }
        }
      } catch {
        // ignore cache parse errors
      }
    if (!forceFresh && lastReportFetchSig.current === sig && allRows.length > 0) {
        setLoading(false);
        return;
      }
      lastReportFetchSig.current = sig;
      if (debugEnabled) {
        // eslint-disable-next-line no-console
        console.debug('[rpr] ws called', { donem: effectiveDonem, baslangic, bitis });
      }
      // TEK WS: raporu bir kere çek, sonrası tamamen client-side.
      // RPR tarafında fatura_tipi genelde "Hepsi" (veya boş/"ALL") olarak bekleniyor.
      // DİA rapor parametrelerinde "Hepsi" çoğu tenant'ta boş string ile temsil edilir.
      // (DAGIT/TUM değerleri sabit; Hepsi = "")
      const raw = await InvoiceService.faturaGetirRaw({ ...basePayload, fatura_tipi: '', force_refresh: forceFresh });
      const rows = extractRows(raw);
      if (debugEnabled) {
        // eslint-disable-next-line no-console
        console.debug('[rpr] ws ok', { rows: rows.length });
      }
      if (invoiceListReqId.current !== myReqId) return; // stale

      const normalized = rows.map((r: any) =>
        normalizeRprRow(r, {
          currencyFallbackCode: '',
          resolveBranchName: (k: number) => String(allBranchNameByKey?.[Number(k)] ?? '').trim(),
          resolveWarehouseName: (k: number) => String(allDepotNameByKey?.[Number(k)] ?? '').trim(),
          targetFirmKey: targetFirmaKodu,
          targetBranchKey: targetSubeKey,
          targetWarehouseKey: targetDepoKey,
          targetPeriodKey: targetDonemKey,
        })
      );

      // Şube/Depo isimleri bazı tenant'larda branches-all/depots-all ile tam örtüşmeyebiliyor.
      // Bu durumda eksik kalan key'leri backend resolve endpoint'i ile tamamla (best-effort).
      try {
        const uniqSube = new Set<number>();
        const uniqDepo = new Set<number>();
        for (const r of normalized) {
          const bk = Number(r.sourceBranchKey);
          const dk = Number(r.sourceWarehouseKey);
          if (Number.isFinite(bk) && bk > 0) uniqSube.add(bk);
          if (Number.isFinite(dk) && dk > 0) uniqDepo.add(dk);
        }
        const subeKeys = Array.from(uniqSube);
        const depoKeys = Array.from(uniqDepo);
        const missingSube = subeKeys.filter(k => !String(allBranchNameByKey?.[k] ?? '').trim());
        const missingDepo = depoKeys.filter(k => !String(allDepotNameByKey?.[k] ?? '').trim());
        if ((missingSube.length > 0 || missingDepo.length > 0) && poolFirmaKodu && effectiveDonem) {
          pushLog({
            source_kalem_key: '',
            status: 'success',
            message: `Şube/depo resolve (ws): missingSube=${missingSube.length} missingDepo=${missingDepo.length}`,
            was_duplicate_override: false,
          });
          const resolved = await InvoiceService.resolveSubeDepoNames({
            firmaKodu: poolFirmaKodu,
            donemKodu: effectiveDonem,
            subeKeys: missingSube.slice(0, 500),
            depoKeys: missingDepo.slice(0, 500),
          });
          if (resolved?.sube && typeof resolved.sube === 'object') {
            setAllBranchNameByKey(prev => {
              const next = { ...(prev ?? {}) } as Record<number, string>;
              for (const [k, v] of Object.entries(resolved.sube)) {
                const nk = Number(k);
                const nv = String(v ?? '').trim();
                if (nk > 0 && nv) next[nk] = nv;
              }
              return next;
            });
          }
          if (resolved?.depo && typeof resolved.depo === 'object') {
            setAllDepotNameByKey(prev => {
              const next = { ...(prev ?? {}) } as Record<number, string>;
              for (const [k, v] of Object.entries(resolved.depo)) {
                const nk = Number(k);
                const nv = String(v ?? '').trim();
                if (nk > 0 && nv) next[nk] = nv;
              }
              return next;
            });
          }
        }
      } catch {}

      // Canlı veri doğrulaması (UI İşlem Günlüğü): dağıtılacak/şube/depo/tür alanları doluyor mu?
      // normalize debug bilgileri kullanıcıya gösterilmez (gerekirse F12'ye).
      if (debugEnabled) {
        try {
          const sampleKeys = Object.keys(rows?.[0] ?? {}).slice(0, 40).join(', ');
          const dyn = normalized.filter(r => String(r.dynamicBranch ?? '').trim() !== '').length;
          // eslint-disable-next-line no-console
          console.debug('[normalize]', { dyn, total: normalized.length, sampleKeys });
        } catch {}
      }
      setAllRows(normalized);
      setFilteredRows(normalized);
      allLineRowsRef.current = normalized;
      filteredLineRowsRef.current = normalized;

      try {
        localStorage.setItem(cacheKey, JSON.stringify({ sig, at: Date.now(), rows }));
      } catch {}
      if (debugEnabled) {
        // eslint-disable-next-line no-console
        console.debug('[rpr] loaded', { rows: normalized.length });
      }

      // Liste yenilendiyse (filtre/değişim/race) eski seçili kalemleri asla taşımayalım.
      setSelectedKalemKeys([]);
    // Liste yenilendi: fatura seçimi sıfırlanır
      setSelectedInvoiceKey(null);
      setActiveInvoice(null);
      setLines([]);
      setSelectedInvoiceKeys(new Set());

    } catch (err: any) {
      if (invoiceListReqId.current !== myReqId) return;
      const msg = err?.response?.data?.message ?? err?.message ?? 'Fatura listesi yüklenemedi.';
      setTransferAlert(msg);
      pushLog({ source_kalem_key: '', status: 'error', message: `Fatura listesi yüklenemedi: ${msg}`, was_duplicate_override: false });
    }
    if (invoiceListReqId.current === myReqId) setLoading(false);
  }, [
    loading,
    poolFirmaKodu,
    sourceDonemKodu,
    periods,
    sourceSubeKey,
    sourceDepoKey,
    ustIslemTuruFilter,
    filterCari,
    filterFaturaNo,
    filterFaturaTuru,
    filterKalemSube,
    filterBaslangic,
    filterBitis,
    allRows.length,
  ]);

  // (Fatura detay WS çağrısı kaldırıldı; kalemler RPR satırlarından üretiliyor.)

  // (Görüntülenecek firma listesi kaldırıldı.)

  /** Havuz / görüntüleyicide başka faturaya geçildiğinde hedef firma — Seçiniz — */
  const resetTargetTransferContext = useCallback(() => {
    setTargetFirmaKodu('');
    setTargetFirmaAdi('');
    setTargetSubeKodu('');
    setTargetSubeKey('');
    setTargetSubeAdi('');
    setTargetDepoKey('');
    setTargetDonemKodu('');
    setTargetDonemKey('');
    setTargetDonemLabel('');
    setTargetResolveInfo('');
    setTargetBranches([]);
    setTargetPeriods([]);
    setTargetDepots([]);
    setAutoTargetLockError(null);
    setTransferAlert(null);
  }, []);

  const openInvoice = async (inv: IInvoiceListRow) => {
    // Aynı faturaya tekrar tıklayınca kalem panelini kapat (toggle).
    const clickedKey = Number(inv.key);
    if (activeInvoice && Number(activeInvoice.key) === clickedKey) {
      setActiveInvoice(null);
      setSelectedInvoiceKey(null);
      setSelectedKalemKeys([]);
      setLines([]);
      return;
    }

    // Kullanıcı başka faturaya geçtiğinde hedef aktarım paneli her zaman sıfırlansın.
    resetTargetTransferContext();

    setActiveInvoice(inv);
    const key = clickedKey;
    setSelectedInvoiceKey(Number.isFinite(key) ? key : null);
    // Kritik: yeni faturaya geçince eski seçimler taşınmasın.
    setSelectedKalemKeys([]);
    setLines([]);
    if (!Number.isFinite(key)) {
      pushLog({ source_kalem_key: '', status: 'error', message: `Kalemler yüklenemedi: geçersiz invoice key (${inv.key})`, was_duplicate_override: false });
      return;
    }
    // WS'e gitmeden: rapor satırlarından kalemleri üret.
    const kalemRows = allLineRowsRef.current
      .filter(r => Number(r.invoiceKey) === Number(key))
      .map(r => r.sourceRow ?? {});
    const mapped = kalemRows.map((r: any, idx: number) => ({
      key: String(r?._key ?? r?.kalem_key ?? idx),
      sirano: Number(r?.sirano ?? idx + 1),
      kalemturu: r?.kalemturu,
      // Not: RPR000000004 çıktısında stok/hizmet kodu alanı yoksa boş kalır.
      stokhizmetkodu: (() => {
        const direct = String(r?.stokhizmetkodu ?? r?.stokkodu ?? r?.hizmetkodu ?? '').trim();
        if (direct) return direct;
        const fk = String(r?._key_scf_fiyatkart ?? '').trim();
        const hit = fk ? stokHizmetByFiyatKartKey[fk] : undefined;
        return String(hit?.kodu ?? '').trim();
      })(),
      stokhizmetaciklama: (() => {
        const direct = String(r?.stokhizmetaciklama ?? '').trim();
        if (direct) return direct;
        const fk = String(r?._key_scf_fiyatkart ?? '').trim();
        const hit = fk ? stokHizmetByFiyatKartKey[fk] : undefined;
        const fromHit = String(hit?.aciklama ?? '').trim();
        return fromHit || String(r?.aciklama1 ?? r?.aciklama2 ?? r?.aciklama3 ?? r?.aciklama ?? '').trim();
      })(),
      // Kolon adı neyse o: miktar ve anamiktar birbirine fallback yapmaz.
      birim: String(
        r?.birim
        ?? r?.birimadi
        ?? r?.birim_adi
        ?? r?.birimAdi
        ?? r?.BIRIM
        ?? r?.BIRIMADI
        ?? r?.BIRIM_ADI
        ?? (() => {
          const k = String(r?._key_scf_kalem_birimleri ?? '').trim();
          if (!k) return '';
          const hit = unitByKey[k];
          return String(hit?.kodu ?? hit?.adi ?? '').trim();
        })()
        ?? ''
      ).trim(),
      // bazı tenantlarda miktar alanları "miktar1/miktar2" veya "anamiktar" üzerinden gelebiliyor
      miktar: Number(r?.miktar ?? r?.MIKTAR ?? r?.miktar1 ?? r?.MIKTAR1 ?? 0) || 0,
      anamiktar: Number(r?.anamiktar ?? r?.ANAMIKTAR ?? r?.ana_miktar ?? r?.ANA_MIKTAR ?? 0) || 0,
      birimfiyati: Number(r?.birimfiyati ?? r?.BIRIMFIYATI ?? r?.birimfiyat ?? r?.BIRIMFIYAT ?? r?.birim_fiyati ?? r?.BIRIM_FIYATI ?? 0) || 0,
      tutari: Number(r?.tutari ?? r?.TUTARI ?? r?.tutar ?? r?.TUTAR ?? r?.satirtutari ?? r?.SATIRTUTARI ?? 0) || 0,
      // Rapor kolonu: kdv (% oran), kdvtutari (tutar)
      kdv: Number(r?.kdv ?? r?.KDV ?? r?.kdvorani ?? r?.KDVORANI ?? r?.kdv_oran ?? r?.KDV_ORAN ?? 0) || 0,
      kdvtutari: Number(
        r?.kdvtutari
        ?? r?.KDV_TUTARI
        ?? r?.kdv_tutari
        ?? r?.kdvtutar
        ?? r?.KDVTUTAR
        ?? r?.kdvduzentutari
        ?? r?.KDVDUZENTUTARI
        ?? 0
      ) || 0,
      indirimtoplam: Number(r?.indirimtoplam ?? r?.toplamindirim ?? r?.indirimtutari ?? 0) || 0,
      indirim1: Number(r?.indirim1 ?? 0) || 0,
      indirim2: Number(r?.indirim2 ?? 0) || 0,
      indirim3: Number(r?.indirim3 ?? 0) || 0,
      indirim4: Number(r?.indirim4 ?? 0) || 0,
      indirim5: Number(r?.indirim5 ?? 0) || 0,
      // SQL: CASE ile üretilen alan (STOK/HIZMET)
      kalem_tipi: String(r?.kalem_tipi ?? (String(r?.kalemturu ?? '').trim() === 'MLZM' ? 'STOK' : String(r?.kalemturu ?? '').trim() === 'HZMT' ? 'HIZMET' : '')).trim(),
      kampanya_kodu: String(r?.kampanya_kodu ?? r?.kampanyakodu ?? '').trim(),
      kalem_doviz_key: Number(r?.kalem_doviz_key ?? r?._key_sis_doviz ?? 0) || 0,
      kalem_doviz_kur: Number(r?.kalem_doviz_kur ?? r?.dovizkuru ?? 0) || 0,
      depoadi: (() => {
        const dk = Number(r?._key_sis_depo_source ?? r?._key_sis_depo_dest ?? 0) || 0;
        // Rapor SQL'e depoadi join eklenirse direkt onu kullan.
        const direct = String(r?.kaynak_depo_adi ?? r?.kaynak_depo_source_adi ?? r?.depoadi ?? '').trim();
        if (direct) return direct;
        return dk ? (allDepotNameByKey[dk] ?? '') : '';
      })(),
      note: String(r?.note ?? r?.NOTE ?? r?.not ?? r?.NOT ?? r?.aciklama ?? '').trim(),
      note2: String(r?.note2 ?? r?.NOTE2 ?? r?.not2 ?? r?.NOT2 ?? r?.aciklama2 ?? '').trim(),
      dinamik_subeler_raw: String(r?.fatsube ?? r?.__dinamik__fatsube ?? '').trim(),
    }));
    setLines(mapped);
  };

  const effectiveDonemKodu = useMemo(() => {
    // Kontör yememek için tarih değişimi dönem değiştirmez.
    // WS yalnız "Verileri Çek" ile çağrılır; tarih sadece client-side filtre olur.
    return Number(sourceDonemKodu) || 0;
  }, [sourceDonemKodu]);

  useEffect(() => {
    if (!poolFirmaKodu || !sourceSubeKey || sourceSubeKey <= 0) {
      setSourceDepots([]);
      setSourceDepoKey(0);
      return;
    }
    // Depo dropdown (filtre) şube bağımlı; dönemden bağımsız geniş depot listesi kullanılır.
    InvoiceService.getDepots(poolFirmaKodu, Number(sourceSubeKey), effectiveDonemKodu || sourceDonemKodu)
      .then(deps => {
        setSourceDepots(deps);
        // Kullanıcı depo seçimini bozma: geçerli değilse boş bırak (auto-select yapma)
        setSourceDepoKey(prev => (prev > 0 && deps.some(d => d.key === prev)) ? prev : 0);
      })
      .catch(() => {
        setSourceDepots([]);
        setSourceDepoKey(0);
      });
  }, [poolFirmaKodu, sourceSubeKey, effectiveDonemKodu, sourceDonemKodu]);

  useEffect(() => {
    if (!poolFirmaKodu) return;
    // Şube/depo isim map'i: tüm dönem birleşimi
    Promise.all([
      InvoiceService.getBranchesAll(poolFirmaKodu),
      InvoiceService.getDepotsAll(poolFirmaKodu),
    ])
      .then(([bs, deps]) => {
        if (bs.length > 0) {
          setBranches(bs);
          const bmap: Record<number, string> = {};
          for (const b of bs) {
            const k = Number((b as any).key);
            if (!k) continue;
            const name = String((b as any).subeadi ?? '').trim();
            if (name && !bmap[k]) bmap[k] = name;
          }
          setAllBranchNameByKey(bmap);
        }
        const map: Record<number, string> = {};
        for (const d of deps) {
          const k = Number(d.key);
          if (!k) continue;
          if (!map[k]) map[k] = String(d.depoadi || '').trim();
        }
        setAllDepotNameByKey(map);
      })
      .catch(() => {
        // ignore
      });
  }, [poolFirmaKodu]);

  useEffect(() => {
    allBranchNameByKeyRef.current = allBranchNameByKey;
  }, [allBranchNameByKey]);

  useEffect(() => {
    allDepotNameByKeyRef.current = allDepotNameByKey;
  }, [allDepotNameByKey]);

  useEffect(() => {
    // Currency lookup kaldırıldı: UI normalize.currencyCode kullanıyor (RPR'den gelir).
  }, [poolFirmaKodu, sourceDonemKodu]);

  // Lookup'lar bazen ilk açılışta boş kalabiliyor; sessizce toparla.
  useEffect(() => {
    if (!poolFirmaKodu) return;
    if (companies.length > 0 && periods.length > 0) return;

    const recoverLookups = async () => {
      try {
        if (companies.length === 0) {
          const comps = await retry(() => InvoiceService.getCompanies());
          setCompanies(prev => (comps.length > 0 ? comps : prev));
        }
      } catch (e) {
        console.warn('[Recover] companies reload failed', e);
      }

      try {
        if (periods.length === 0) {
          const ps = await retry(() => InvoiceService.getPeriods(poolFirmaKodu));
          setPeriods(ps);
          if (ps.length > 0 && !ps.some(x => x.donemkodu === sourceDonemKodu)) {
            const p =
              (defaultSourceDonemKodu > 0 ? ps.find(x => x.donemkodu === defaultSourceDonemKodu) : undefined)
              ?? ps.find(x => x.ontanimli)
              ?? [...ps].sort((a, b) => b.donemkodu - a.donemkodu)[0];
            if (p) setSourceDonemKodu(p.donemkodu);
          }
        }
      } catch (e) {
        console.warn('[Recover] periods reload failed', e);
      }
    };

    recoverLookups();
  }, [poolFirmaKodu, companies.length, periods.length, sourceDonemKodu, defaultSourceDonemKodu]);

  // Şube lookup bazen ilk açılışta boş kalabiliyor; isim map'i artık branches-all/depots-all ile yüklenir.

  // DİA'da sonradan açılan firmaların dropdown'a yansıması için periyodik tazeleme.
  useEffect(() => {
    const timer = setInterval(() => {
      ensureCompaniesLoaded(true);
    }, 60000);
    return () => clearInterval(timer);
  }, [ensureCompaniesLoaded]);

  useEffect(() => {
    // Seçimler temizlensin ama liste boşaltılmasın.
    resetSelectionState(false);
  }, [poolFirmaKodu, sourceDonemKodu, sourceSubeKey, sourceDepoKey, resetSelectionState]);

  // Not: RPR çağrısı kontörlü olduğu için otomatik listeleme kapalı.
  // Kullanıcı sadece "Verileri Çek" butonuyla fetchInvoices() çağırır.

  useEffect(() => {
    resetSelectionState(false);
  }, [filterFaturaNo, filterCari, filterFaturaTuru, transferTypeFilter, resetSelectionState]);

  // Kullanıcı "dönem" seçtiğinde listede "yanlış fatura" görünmesin:
  // Varsayılan tarih filtresini seçili dönemin tarih aralığına çek (sadece filtre boşsa).
  useEffect(() => {
    if (!sourceDonemKodu || periods.length === 0) return;
    const p = periods.find(x => Number(x.donemkodu) === Number(sourceDonemKodu));
    const pBas = String(p?.baslangic_tarihi ?? '').slice(0, 10);
    const pBit = String(p?.bitis_tarihi ?? '').slice(0, 10);
    if (!filterBaslangic && pBas) setFilterBaslangic(pBas);
    if (!filterBitis && pBit) setFilterBitis(pBit);
  }, [sourceDonemKodu, periods, filterBaslangic, filterBitis]);

  // Kullanıcı tarih aralığı seçtiğinde dönem kodunu otomatik bul (WS sadece butonda).
  useEffect(() => {
    if (!filterBaslangic || periods.length === 0) return;
    const b = Date.parse(String(filterBaslangic).slice(0, 10));
    const e = filterBitis ? Date.parse(String(filterBitis).slice(0, 10)) : b;
    if (Number.isNaN(b)) return;

    const pick = periods.find(p => {
      const pb = Date.parse(String(p.baslangic_tarihi ?? '').slice(0, 10));
      const pe = Date.parse(String(p.bitis_tarihi ?? '').slice(0, 10));
      if (Number.isNaN(pb) || Number.isNaN(pe)) return false;
      return pb <= b && pe >= e;
    });
    if (pick && Number(pick.donemkodu) > 0 && Number(pick.donemkodu) !== Number(sourceDonemKodu)) {
      setSourceDonemKodu(Number(pick.donemkodu));
    }
  }, [filterBaslangic, filterBitis, periods, sourceDonemKodu]);

  const resolveTargetForFirma = useCallback(async (fk: number) => {
    if (!fk || !Number.isFinite(fk) || fk <= 0) return;
    // Kaynak fatura tarihi yoksa resolve-target çağırma (DİA 400 / dönem bulunamadı spamını önler).
    const srcDate = activeInvoice?.tarih;
    if (!srcDate) return;
    const cacheKey = `${fk}|${Number(sourceDonemKodu) || 0}|${String(srcDate).slice(0, 10)}`;
    try {
      const found = companies.find(c => c.firma_kodu === fk);
      setTargetFirmaAdi(found?.firma_adi ?? '');
      // Resolve cache (WS tekrarını engelle)
      const cached = resolveTargetCacheRef.current.get(cacheKey);
      const res = cached ?? await retry(() => InvoiceService.resolveTarget({
          targetFirmaKodu: fk,
          sourceDonemKodu: Number(sourceDonemKodu) || undefined,
          sourceInvoiceDate: srcDate,
        }), 2);
      if (!cached) resolveTargetCacheRef.current.set(cacheKey, res);

      // Auto-select: sadece backend gerçekten seçim yaptıysa (tek şube+tek depo).
      if ((targetSubeKey === '' || !Number.isFinite(Number(targetSubeKey))) && Number(res.targetSubeKey) > 0) {
        setTargetSubeKey(res.targetSubeKey);
        setTargetSubeKodu(String(res.targetSubeKey));
      }
      if (Number(res.targetSubeKey) > 0) setTargetSubeAdi(res.targetSubeAdi);
      if ((targetDepoKey === '' || !Number.isFinite(Number(targetDepoKey))) && Number(res.targetDepoKey) > 0) {
        setTargetDepoKey(res.targetDepoKey);
      }
      setTargetDonemKodu(res.targetDonemKodu);
      if (targetDonemKey === '' || !Number.isFinite(Number(targetDonemKey))) setTargetDonemKey(res.targetDonemKey);
      setTargetDonemLabel(res.targetDonemLabel);
      setTargetResolveInfo(res.fallbackUsed ? (res.fallbackReason ?? 'Fallback kullanıldı.') : 'Otomatik hedef çözümleme başarılı.');
      setAutoTargetLockError(null);
      if (!targetDonemLabel) setTargetDonemLabel(res.targetDonemLabel);
      // Not: listeleri burada tek elemana düşürmeyelim; dropdown'lar gerçek listeden dolsun.
      // Eğer listeler henüz yüklenmediyse effect'ler dolduracak.
    } catch (err: any) {
      const code = err?.response?.data?.code ? String(err.response.data.code) : '';
      const msg = err?.response?.data?.message ?? 'Hedef firma çözümlemesi başarısız.';
      const details = err?.response?.data?.details ? String(err.response.data.details) : '';
      const extra = [code && `code=${code}`, details].filter(Boolean).join(' | ');
      const fullMsg = extra ? `${msg} (${extra})` : msg;
      // Geçici DİA dalgalanmasında mevcut hedef seçimini silme; kullanıcı aktarımı kaybetmesin.
      setTargetResolveInfo('');
      setAutoTargetLockError(fullMsg);
      // Hata olsa bile daha sonra kullanıcı manuel seçebilsin diye listeleri silmeyelim.
    }
  }, [companies, sourceDonemKodu, activeInvoice?.tarih, targetSubeKey, targetDepoKey, targetDonemKey, targetDonemLabel]);

  const resolveTargetReqId = useRef(0);
  const lastResolveSignature = useRef<string>('');
  const resolveTargetCacheRef = useRef<Map<string, any>>(new Map());

  // ── Hedef firma değişince backend auto-resolve ─────────────────────────────
  const handleTargetFirmaChange = useCallback((firmaCodeStr: string) => {
    const fk = parseInt(firmaCodeStr);
    setTransferAlert(null);
    setTargetFirmaKodu(fk || '');
    setTargetFirmaAdi(fk ? (companies.find(c => c.firma_kodu === fk)?.firma_adi ?? '') : '');
    setTargetSubeKodu('');
    setTargetSubeKey('');
    setTargetSubeAdi('');
    setTargetDepoKey('');
    setTargetDonemKodu('');
    setTargetDonemKey('');
    setTargetDonemLabel('');
    setTargetResolveInfo('');
    setTargetBranches([]);
    setTargetPeriods([]);
    setTargetDepots([]);
    setAutoTargetLockError(null);
    // resolve-target: useEffect[targetFirmaKodu, fatura tarihi] tetiklenir (çift çağrı yok).
  }, [companies]);

  // Hedef firma seçilince şube/dönem listelerini yükle (fatura seçimi olmasa bile).
  useEffect(() => {
    const fk = Number(targetFirmaKodu);
    if (!Number.isFinite(fk) || fk <= 0) return;
    let cancelled = false;
    (async () => {
      try {
        const [ps, bs] = await Promise.all([
          retry(() => InvoiceService.getPeriods(fk), 2),
          retry(() => InvoiceService.getBranches(fk), 2),
        ]);
        if (cancelled) return;
        setTargetPeriods(ps);
        setTargetBranches(bs);
        // Mevcut seçim listede yoksa temizle; boşsa otomatik seç.
        setTargetDonemKey(prev => {
          const keep = prev !== '' && ps.some(p => Number(p.key) === Number(prev));
          if (keep) return prev;
          const preferred = ps.find(p => p.ontanimli) ?? ps[0];
          setTargetDonemLabel(preferred?.gorunenkod ?? '');
          setTargetDonemKodu(preferred?.donemkodu ?? '');
          return preferred ? Number(preferred.key) : '';
        });
        setTargetSubeKey(prev => {
          const keep = prev !== '' && bs.some(b => Number(b.key) === Number(prev));
          if (keep) return prev;
          // Kullanıcı isteği: 1'den fazla şube varsa otomatik seçme.
          if (bs.length === 1) {
            setTargetSubeAdi(bs[0]?.subeadi ?? '');
            return Number(bs[0]?.key ?? 0) || '';
          }
          setTargetSubeAdi('');
          return '';
        });
      } catch {
        if (cancelled) return;
        setTargetPeriods([]);
        setTargetBranches([]);
      }
    })();
    return () => { cancelled = true; };
  }, [targetFirmaKodu]);

  // Hedef şube seçilince depoları yükle.
  useEffect(() => {
    const fk = Number(targetFirmaKodu);
    const sk = Number(targetSubeKey);
    if (!Number.isFinite(fk) || fk <= 0 || !Number.isFinite(sk) || sk <= 0) {
      setTargetDepots([]);
      setTargetDepoKey('');
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const deps = await retry(() => InvoiceService.getDepots(fk, sk), 2);
        if (cancelled) return;
        setTargetDepots(deps);
        setTargetDepoKey(prev => {
          const keep = prev !== '' && deps.some(d => Number(d.key) === Number(prev));
          if (keep) return prev;
          // Kullanıcı isteği: 1'den fazla depo varsa otomatik seçme.
          if (deps.length === 1) {
            return Number(deps[0]?.key ?? 0) || '';
          }
          return '';
        });
      } catch {
        if (cancelled) return;
        setTargetDepots([]);
        setTargetDepoKey('');
      }
    })();
    return () => { cancelled = true; };
  }, [targetFirmaKodu, targetSubeKey]);

  // Yalnızca hedef firma değişince çözümle; fatura tıklaması hedef alanları sıfırlamasın (400/resolve döngüsü önlenir).
  useEffect(() => {
    const fk = Number(targetFirmaKodu);
    if (!targetFirmaKodu || !Number.isFinite(fk) || fk <= 0) return;
    const sig = `${fk}|${activeInvoice?.tarih ?? ''}|${sourceDonemKodu}`;
    if (!activeInvoice?.tarih) return;
    if (lastResolveSignature.current === sig) return;
    lastResolveSignature.current = sig;

    const my = ++resolveTargetReqId.current;
    const t = setTimeout(() => {
      if (resolveTargetReqId.current !== my) return;
      resolveTargetForFirma(fk);
    }, 250);
    return () => clearTimeout(t);
  }, [targetFirmaKodu, activeInvoice?.tarih, sourceDonemKodu, resolveTargetForFirma]);

  const debugEnabled = useMemo(() => {
    try { return localStorage.getItem('debug_logs_v1') === '1'; } catch { return false; }
  }, []);

  const pushLog = useCallback((log: ITransferLogEntry) => {
    // Kullanıcı günlükte sadece kritik şeyleri görsün.
    const msg = String(log?.message ?? '');
    const isImportant =
      log.status === 'error' ||
      log.status === 'duplicate' ||
      msg.startsWith('Uyarı:') ||
      msg.startsWith('Aktarım başarısız') ||
      msg.startsWith('Aktarım Özeti') ||
      msg.startsWith('Progress:') ||
      msg.includes('SNAPSHOT') ||
      msg.includes('mükerrer') ||
      msg.includes('MÜKERRER');

    if (isImportant) {
      setLogs(prev => [log, ...prev].slice(0, 60));
    } else if (debugEnabled) {
      // eslint-disable-next-line no-console
      console.debug('[debug-log]', log);
    }
  }, [debugEnabled]);

  // Not: dönem tarih aralığından otomatik seçme kaldırıldı (tarih sadece client-side filtre).

  const applyFilters = useCallback(() => {
    const contains = (a: any, b: any) =>
      (a ?? '').toString().toLowerCase().includes((b ?? '').toString().toLowerCase());
    const eq = (a: any, b: any) => (a ?? '').toString().trim().toLowerCase() === (b ?? '').toString().trim().toLowerCase();
    const cleanDyn = (v: any) => {
      let x = String(v ?? '');
      // görünmeyen/boşluk karakterleri (NBSP + zero-width)
      x = x.replace(/[\u200B-\u200D\uFEFF]/g, '').replace(/\u00a0/g, ' ');
      x = x.replace(/\s+/g, ' ').trim();
      const u = x.toUpperCase();
      if (!x) return '';
      if (/^[-—–]+$/.test(x)) return '';
      if (u === '0' || u === 'NULL' || u === 'UNDEFINED' || u === 'NONE' || u === 'N/A') return '';
      return x;
    };

    const base = allRows;
    let data = [...base];

    // Önce: local registry (aktarılan) satırlarını işaretle.
    data = data.map(r => {
      const ik = Number(r.invoiceKey);
      const lk = Number(r.lineKey);
      if (Number.isFinite(ik) && ik > 0 && Number.isFinite(lk) && lk > 0) {
        const key = `${ik}:${lk}`;
        if (transferredLineIndexRef.current.has(key)) {
          return { ...r, transferStatus: 'Aktarıldı', dynamicBranch: cleanDyn(r.dynamicBranch) };
        }
      }
      // Kritik: UI'da "boş" görünen ama görünmez karakter taşıyan dinamik şube değerlerini tek yerde temizle.
      // Böylece hem görüntü hem de "Dağıtılacak/Tüm" kararı aynı değerden yürür.
      if (typeof r.dynamicBranch !== 'string' || cleanDyn(r.dynamicBranch) !== String(r.dynamicBranch ?? '')) {
        return { ...r, dynamicBranch: cleanDyn(r.dynamicBranch) };
      }
      return r;
    });

    // Aynı faturanın farklı kalemlerinde bazı alanlar boş gelebiliyor.
    // Bu yüzden fatura bazında "en dolu" meta değerleri toplayıp hem filtrede hem listede kullanıyoruz.
    const invoiceMeta = new Map<number, { upperCode: string; upperName: string; branchName: string; depotName: string; typeLabel: string }>();
    const score = (m: { upperCode: string; upperName: string; branchName: string; depotName: string; typeLabel: string }) =>
      (m.upperCode ? 2 : 0) + (m.upperName ? 2 : 0) + (m.branchName ? 2 : 0) + (m.depotName ? 2 : 0) + (m.typeLabel ? 1 : 0);
    for (const r of base) {
      const ik = Number(r.invoiceKey);
      if (!Number.isFinite(ik) || ik <= 0) continue;
      const m = {
        upperCode: String(r.upperProcessCode ?? '').trim(),
        upperName: String(r.upperProcessName ?? '').trim(),
        branchName:
          String(allBranchNameByKey?.[Number(r.sourceBranchKey)] ?? '').trim() ||
          String(r.sourceBranchName ?? '').trim(),
        depotName:
          String(allDepotNameByKey?.[Number(r.sourceWarehouseKey)] ?? '').trim() ||
          String(r.sourceWarehouseName ?? '').trim(),
        typeLabel: String(r.invoiceTypeLabel ?? '').trim(),
      };
      const prev = invoiceMeta.get(ik);
      if (!prev || score(m) > score(prev)) invoiceMeta.set(ik, m);
    }
    const eff = (r: NormalizedRprRow) => {
      const ik = Number(r.invoiceKey);
      const m = invoiceMeta.get(ik);
      return {
        upperCode: String(r.upperProcessCode ?? '').trim() || (m?.upperCode ?? ''),
        upperName: String(r.upperProcessName ?? '').trim() || (m?.upperName ?? ''),
        branchName:
          String(allBranchNameByKey?.[Number(r.sourceBranchKey)] ?? '').trim() ||
          String(r.sourceBranchName ?? '').trim() ||
          (m?.branchName ?? '') ||
          (Number(r.sourceBranchKey) > 0 ? 'Bilinmiyor' : ''),
        depotName:
          String(allDepotNameByKey?.[Number(r.sourceWarehouseKey)] ?? '').trim() ||
          String(r.sourceWarehouseName ?? '').trim() ||
          (m?.depotName ?? '') ||
          (Number(r.sourceWarehouseKey) > 0 ? 'Bilinmiyor' : ''),
        typeLabel: String(r.invoiceTypeLabel ?? '').trim() || (m?.typeLabel ?? ''),
      };
    };

    const cariAdi = (filterCari ?? '').toString().trim();
    const faturaNo = (filterFaturaNo ?? '').toString().trim();
    const faturaTuru = (filterFaturaTuru ?? '').toString().trim();
    const kalemSube = (filterKalemSube ?? '').toString().trim();
    const durum = (filterDurum ?? '').toString().trim();

    if (cariAdi) data = data.filter(r => contains(r.cariName, cariAdi));
    if (faturaNo) data = data.filter(r => contains(r.invoiceNo, faturaNo));
    if (faturaTuru) data = data.filter(r => contains(eff(r).typeLabel, faturaTuru));
    if (kalemSube) data = data.filter(r => contains(cleanDyn(r.dynamicBranch), kalemSube));

    // Üst işlem filtresi local cache üzerinde (kodu + açıklama)
    if ((ustIslemTuruFilter ?? '').toString().trim()) {
      const q = (ustIslemTuruFilter ?? '').toString().trim();
      // Dropdown mantığı: "A/B/..." gibi seçimlerde sadece KOD exact match olmalı.
      // Aksi halde isimde geçen "a" harfi yüzünden her şey eşleşiyor.
      const isLikelyCode = q.length <= 3 && /^[a-z0-9]+$/i.test(q) && q.toUpperCase() !== 'TUM';
      if (isLikelyCode) {
        // Dağıtılacak satırlarda üst işlem satır bazında boş olsa bile aynı faturanın başka kaleminden doldurabiliriz.
        data = data.filter(r => {
          const e = eff(r);
          return eq(e.upperCode, q) || eq(e.upperName, q);
        });
      } else {
        data = data.filter(r => {
          const e = eff(r);
          return contains(e.upperCode, q) || contains(e.upperName, q);
        });
      }
    }

    // Şube/Depo filtreleri local cache üzerinde (key üzerinden - daha güvenilir)
    if (Number(sourceSubeKey) > 0) {
      const k = Number(sourceSubeKey);
      data = data.filter(r => Number(r.sourceBranchKey) === k);
    }
    if (Number(sourceDepoKey) > 0) {
      const k = Number(sourceDepoKey);
      data = data.filter(r => Number(r.sourceWarehouseKey) === k);
    }

    // Tarih aralığı client-side (ISO + dd.MM.yyyy)
    if ((filterBaslangic ?? '').toString().trim()) {
      const b = parseFlexibleDateMs(filterBaslangic);
      if (!Number.isNaN(b)) data = data.filter(r => parseFlexibleDateMs(r.date) >= b);
    }
    if ((filterBitis ?? '').toString().trim()) {
      const e = parseFlexibleDateMs(filterBitis);
      if (!Number.isNaN(e)) data = data.filter(r => parseFlexibleDateMs(r.date) <= e);
    }

    // Aktarım durumu (Bekleyen/Kısmi/Aktarıldı)
    if (durum) {
      if (durum === '0') data = data.filter(r => String(r.transferStatus).toLowerCase().includes('bekliyor'));
      if (durum === '1') data = data.filter(r => String(r.transferStatus).toLowerCase().includes('kısmi') || String(r.transferStatus).toLowerCase().includes('kismi'));
      if (durum === '2') data = data.filter(r => String(r.transferStatus).toLowerCase().includes('aktar'));
    }

    // satır-bazlı filtrelenmiş veri (kalem listesi) aktarım için saklanır
    filteredLineRowsRef.current = data;
    setFilteredRows(data);

    // Debug filtre metrikleri: sadece F12 (kullanıcı günlüğüne yazma).
    if (debugEnabled) {
      try {
        // eslint-disable-next-line no-console
        console.debug('[filters]', {
          rowsIn: base.length,
          rowsOut: data.length,
          tab: transferTypeFilter,
          ustIslem: String(ustIslemTuruFilter ?? ''),
          faturaTuru: String(filterFaturaTuru ?? ''),
          subeKey: Number(sourceSubeKey) || 0,
          depoKey: Number(sourceDepoKey) || 0,
          cari: String(filterCari ?? ''),
          faturaNo: String(filterFaturaNo ?? ''),
        });
      } catch {}
    }
  }, [
    allRows,
    allBranchNameByKey,
    allDepotNameByKey,
    filterCari,
    filterFaturaNo,
    filterFaturaTuru,
    filterKalemSube,
    transferTypeFilter,
    sourceSubeKey,
    sourceDepoKey,
    ustIslemTuruFilter,
    filterBaslangic,
    filterBitis,
    filterDurum,
    branches,
    sourceDepots,
  ]);

  useEffect(() => {
    applyFilters();
  }, [applyFilters]);

  // Not: console debug spam kaldırıldı (İşlem Günlüğü kullanılmalı).

  const invoiceTypeOptions = useMemo(() => {
    const set = new Set<string>();
    for (const r of allRows) {
      const v = String(r.invoiceTypeLabel ?? '').trim();
      if (v) set.add(v);
    }
    return Array.from(set)
      .sort((a, b) => a.localeCompare(b, 'tr'))
      .map(v => ({ value: v, label: v }));
  }, [allRows]);

  const invoiceTypesLoading = false;

  const displayedRows = useMemo(() => {
    const dirMul = invoiceSort.dir === 'asc' ? 1 : -1;
    const cmpStr = (a: any, b: any) =>
      String(a ?? '').trim().localeCompare(String(b ?? '').trim(), 'tr', { numeric: true, sensitivity: 'base' }) * dirMul;
    const cmpNum = (a: any, b: any) => {
      const aa = Number(a ?? 0);
      const bb = Number(b ?? 0);
      if (!Number.isFinite(aa) && !Number.isFinite(bb)) return 0;
      if (!Number.isFinite(aa)) return -1 * dirMul;
      if (!Number.isFinite(bb)) return 1 * dirMul;
      return (aa - bb) * dirMul;
    };
    const cmpDate = (a: any, b: any) => {
      const aa = parseFlexibleDateMs(a);
      const bb = parseFlexibleDateMs(b);
      if (Number.isNaN(aa) && Number.isNaN(bb)) return 0;
      if (Number.isNaN(aa)) return -1 * dirMul;
      if (Number.isNaN(bb)) return 1 * dirMul;
      return (aa - bb) * dirMul;
    };

    const cleanDyn = (v: any) => {
      let x = String(v ?? '');
      x = x.replace(/[\u200B-\u200D\uFEFF]/g, '').replace(/\u00a0/g, ' ');
      x = x.replace(/\s+/g, ' ').trim();
      const u = x.toUpperCase();
      if (!x) return '';
      if (/^[-—–]+$/.test(x)) return '';
      if (u === '0' || u === 'NULL' || u === 'UNDEFINED' || u === 'NONE' || u === 'N/A') return '';
      return x;
    };

    // MODE 2: Dağıtılacak (kalem bazlı) => dynamicBranch gerçekten dolu
    if (transferTypeFilter === 'dagitilacak_faturalar') {
      const rows = filteredRows.filter(r => !r.invalid && cleanDyn(r.dynamicBranch) !== '');
      if (!invoiceSort.key) return rows;
      const key = invoiceSort.key;
      return [...rows].sort((a, b) => {
        switch (key) {
          case 'invoiceNo': return cmpStr(a.invoiceNo, b.invoiceNo);
          case 'fisNo': return cmpStr(a.fisNo, b.fisNo);
          case 'date': return cmpDate(a.date, b.date);
          case 'invoiceType': return cmpStr(a.invoiceTypeLabel, b.invoiceTypeLabel);
          case 'upperProcess': return cmpStr(a.upperProcessName || a.upperProcessCode, b.upperProcessName || b.upperProcessCode);
          case 'cariName': return cmpStr(a.cariName, b.cariName);
          case 'sourceBranch': return cmpStr(allBranchNameByKey?.[Number(a.sourceBranchKey)] ?? a.sourceBranchName, allBranchNameByKey?.[Number(b.sourceBranchKey)] ?? b.sourceBranchName);
          case 'sourceDepot': return cmpStr(allDepotNameByKey?.[Number(a.sourceWarehouseKey)] ?? a.sourceWarehouseName, allDepotNameByKey?.[Number(b.sourceWarehouseKey)] ?? b.sourceWarehouseName);
          case 'currency': return cmpStr(a.currencyCode, b.currencyCode);
          case 'total': return cmpNum(a.invoiceTotal, b.invoiceTotal);
          case 'discount': return cmpNum(a.invoiceDiscountTotal, b.invoiceDiscountTotal);
          case 'expense': return cmpNum(a.invoiceExpenseTotal, b.invoiceExpenseTotal);
          case 'vat': return cmpNum(a.invoiceVat, b.invoiceVat);
          case 'net': return cmpNum(a.invoiceNet, b.invoiceNet);
          case 'transferStatus': return cmpStr(a.transferStatus, b.transferStatus);
          case 'dynamicBranch': return cmpStr(a.dynamicBranch, b.dynamicBranch);
          default: return 0;
        }
      });
    }

    // MODE 1: Tüm Faturalar => "kalem şube seçili olmayan" satırlardan groupBy(invoiceKey || invoiceNo)
    const map = new Map<string, NormalizedRprRow>();
    const repScore = (r: NormalizedRprRow) => {
      const b = String(allBranchNameByKey?.[Number(r.sourceBranchKey)] ?? '').trim() || String(r.sourceBranchName ?? '').trim();
      const d = String(allDepotNameByKey?.[Number(r.sourceWarehouseKey)] ?? '').trim() || String(r.sourceWarehouseName ?? '').trim();
      const u = String(r.upperProcessCode ?? '').trim() || String(r.upperProcessName ?? '').trim();
      const t = String(r.invoiceTypeLabel ?? '').trim();
      return (b ? 2 : 0) + (d ? 2 : 0) + (u ? 2 : 0) + (t ? 1 : 0);
    };
    for (const r of filteredRows) {
      // KURAL: Kalemde şube seçili (dinamik) olanlar "Dağıtılacak" moduna aittir.
      // "Tüm Faturalar" listesi, kalem şube seçili olmayan satırlardan oluşur.
      if (cleanDyn(r.dynamicBranch) !== '') continue;
      const groupKey = (Number(r.invoiceKey) > 0 ? String(r.invoiceKey) : '') || String(r.invoiceNo || '').trim();
      if (!groupKey) continue;
      // invoice satırı temsilcisi: invalid varsa onu da göstereceğiz (kırmızı), ama seçim disable olacak
      if (!map.has(groupKey)) map.set(groupKey, r);
      else {
        const cur = map.get(groupKey)!;
        if (repScore(r) > repScore(cur)) map.set(groupKey, r);
      }
    }
    const rows = Array.from(map.values());
    if (!invoiceSort.key) return rows;
    const key = invoiceSort.key;
    return [...rows].sort((a, b) => {
      switch (key) {
        case 'invoiceNo': return cmpStr(a.invoiceNo, b.invoiceNo);
        case 'fisNo': return cmpStr(a.fisNo, b.fisNo);
        case 'date': return cmpDate(a.date, b.date);
        case 'invoiceType': return cmpStr(a.invoiceTypeLabel, b.invoiceTypeLabel);
        case 'upperProcess': return cmpStr(a.upperProcessName || a.upperProcessCode, b.upperProcessName || b.upperProcessCode);
        case 'cariName': return cmpStr(a.cariName, b.cariName);
        case 'sourceBranch': return cmpStr(allBranchNameByKey?.[Number(a.sourceBranchKey)] ?? a.sourceBranchName, allBranchNameByKey?.[Number(b.sourceBranchKey)] ?? b.sourceBranchName);
        case 'sourceDepot': return cmpStr(allDepotNameByKey?.[Number(a.sourceWarehouseKey)] ?? a.sourceWarehouseName, allDepotNameByKey?.[Number(b.sourceWarehouseKey)] ?? b.sourceWarehouseName);
        case 'currency': return cmpStr(a.currencyCode, b.currencyCode);
        case 'total': return cmpNum(a.invoiceTotal, b.invoiceTotal);
        case 'discount': return cmpNum(a.invoiceDiscountTotal, b.invoiceDiscountTotal);
        case 'expense': return cmpNum(a.invoiceExpenseTotal, b.invoiceExpenseTotal);
        case 'vat': return cmpNum(a.invoiceVat, b.invoiceVat);
        case 'net': return cmpNum(a.invoiceNet, b.invoiceNet);
        case 'transferStatus': return cmpStr(a.transferStatus, b.transferStatus);
        default: return 0;
      }
    });
  }, [filteredRows, transferTypeFilter, allBranchNameByKey, allDepotNameByKey, invoiceSort.dir, invoiceSort.key]);

  /**
   * “Tümünü seç” kapsamı: filtrelenmiş tüm satırlardaki tekil fatura _key’leri (ilk 500 görünüm sınırı değil).
   */
  const poolPageSelectableInvoiceKeys = useMemo(() => {
    const keys = new Set<number>();
    for (const row of filteredRows) {
      const invKey = Number(row.invoiceKey);
      if (Number.isFinite(invKey) && invKey > 0) keys.add(invKey);
    }
    return keys;
  }, [filteredRows]);

  const normalizeLineKey = (k: string | number | null | undefined) => String(k ?? '');

  const toggleInvoiceKey = useCallback((invoiceKey: number) => {
    const ik = Number(invoiceKey);
    if (!Number.isFinite(ik) || ik <= 0) return;
    setSelectedInvoiceKeys(prev => {
      const next = new Set(prev);
      if (next.has(ik)) next.delete(ik);
      else next.add(ik);
      return next;
    });
  }, []);

  const toggleLineKey = useCallback((lineKey: string | number) => {
    const lk = String(lineKey ?? '').trim();
    if (!lk || lk === '0') return;
    setSelectedLineKeysManual(prev => {
      const next = new Set(prev);
      if (next.has(lk)) next.delete(lk);
      else next.add(lk);
      return next;
    });
  }, []);

  const toggleAllVisibleInvoices = () => {
    if (poolPageSelectableInvoiceKeys.size === 0) return;
    setSelectedInvoiceKeys(prev => {
      const next = new Set(prev);
      const allOn = [...poolPageSelectableInvoiceKeys].every(k => next.has(k));
      if (allOn) for (const k of poolPageSelectableInvoiceKeys) next.delete(k);
      else for (const k of poolPageSelectableInvoiceKeys) next.add(k);
      return next;
    });
  };

  const toggleAllVisibleLines = () => {
    // Dağıtılacak(Kalem) listesinde görünür satırların kalem key’lerini seç/kaldır
    const keys = new Set<string>();
    for (const r of displayedRows) {
      const lk = String(r.lineKey ?? '').trim();
      if (lk && lk !== '0') keys.add(lk);
    }
    if (keys.size === 0) return;
    setSelectedLineKeysManual(prev => {
      const next = new Set(prev);
      const allOn = [...keys].every(k => next.has(k));
      if (allOn) for (const k of keys) next.delete(k);
      else for (const k of keys) next.add(k);
      return next;
    });
  };

  // ── Seçili kalem toplamları ───────────────────────────────────────────────
  const selectedLines = useMemo(() =>
    lines.filter(l => selectedKalemKeys.includes(normalizeLineKey(l.key))),
    [lines, selectedKalemKeys]
  );

  const calculatedTransferType = useMemo(() => 'FATURA', []);

  const transferDecisionReason = useMemo(() => {
    return 'KURAL: Kalemde şube seçimi olsa bile hedefe her zaman FATURA aktarılır (şube sadece hedef şube eşleştirmesi içindir).';
  }, [calculatedTransferType]);

  const isDistributableMode = transferTypeFilter === 'dagitilacak_faturalar';

  const activeInvoiceRprLines = useMemo(() => {
    const k = Number(selectedInvoiceKey ?? 0);
    if (!Number.isFinite(k) || k <= 0) return [];
    return allLineRowsRef.current.filter(r => Number(r.invoiceKey) === k);
  }, [selectedInvoiceKey, allRows]);

  const [autoTargetLockError, setAutoTargetLockError] = useState<string | null>(null);

  const distributionSummary = useMemo(() => {
    if (!isDistributableMode) return [];
    const header = (activeInvoice?.sourcesubeadi ?? '').trim();
    const counts = new Map<string, number>();
    for (const l of selectedLines) {
      const eff = (l.dinamik_subeler_normalized || l.dinamik_subeler_raw || '').trim() || header || '—';
      counts.set(eff, (counts.get(eff) ?? 0) + 1);
    }
    return Array.from(counts.entries())
      .map(([branch, count]) => ({ branch, count }))
      .sort((a, b) => b.count - a.count || a.branch.localeCompare(b.branch, 'tr'));
  }, [isDistributableMode, selectedLines, activeInvoice?.sourcesubeadi]);

  const targetSelectionRuleError = '';
  const allowedTargetBranches = targetBranches;
  const allowedTargetDepots = targetDepots;

  // ── Duplicate risk (uygulama özel hesaplama) ──────────────────────────────
  const duplicateRiskCount = 0;


  // ── Transfer butonu kontrol ───────────────────────────────────────────────
  // Not: Bu sürümde gerçek aktarım yok; ama UI enable koşulunu doğru hesaplıyoruz.
  const canListSource = Boolean(poolFirmaKodu) && Boolean(sourceDonemKodu);
  /** Seçili satır kümesi — snapshot/VERİ süzmesi yok (doğrulama backend). */
  const selectedPoolRows = useMemo(
    () => allRows.filter(r => selectedLineKeys.has(String(r.lineKey))),
    [allRows, selectedLineKeys]
  );

  /** Havuz (RPR): seçim selectedInvoiceKeys → selectedLineKeys türetilir — legacy `lines` boşken toplam yine görünsün. */
  const selectedTotal = useMemo(() => {
    if (selectedPoolRows.length > 0) {
      return selectedPoolRows.reduce((s, r) => s + (Number(r.lineTotal ?? 0) || 0), 0);
    }
    return selectedLines.reduce((s, l) => s + (l.tutari ?? 0), 0);
  }, [selectedPoolRows, selectedLines]);

  /** KDV: detay satırından kdvtutari varsa onu kullan; RPR’da brut+kdv oranından yaklaşık. */
  const selectedKdvTotal = useMemo(() => {
    if (selectedPoolRows.length > 0) {
      return selectedPoolRows.reduce((s, r) => {
        const lt = Number(r.lineTotal ?? 0) || 0;
        const pct = Number(r.lineKdvPercent ?? 0) || 0;
        if (lt <= 0 || pct <= 0) return s;
        return s + (lt * pct) / (100 + pct);
      }, 0);
    }
    return selectedLines.reduce((s, l) => s + (l.kdvtutari ?? 0), 0);
  }, [selectedPoolRows, selectedLines]);

  const canTransfer =
    canListSource &&
    !(sourceSubeKey === 0 && sourceDepoKey !== 0) &&
    selectedPoolRows.length > 0 &&
    Boolean(targetFirmaKodu) &&
    Boolean(targetDonemKodu) &&
    Boolean(targetSubeKey) &&
    Boolean(targetDepoKey) &&
    !Boolean(autoTargetLockError) &&
    !targetSelectionRuleError;

  // Not: Havuzda seçim fatura bazlı; selectedLineKeys türetilir (selectedKalemKeys legacy).

  const transferBlockers = useMemo(() => {
    const blockers: string[] = [];
    if (selectedInvoiceKeys.size === 0) blockers.push('Havuzdan aktarılacak fatura seçilmedi (üst listedeki onay kutusu).');
    if (autoTargetLockError) blockers.push(autoTargetLockError);
    if (targetSelectionRuleError) blockers.push(targetSelectionRuleError);
    if (!targetFirmaKodu) blockers.push('Hedef firma seçin');
    if (targetFirmaKodu && !targetSubeKey) blockers.push('Hedef şube otomatik bulunamadı');
    if (targetFirmaKodu && !targetDepoKey) blockers.push('Hedef depo otomatik bulunamadı');
    if (targetFirmaKodu && !targetDonemKodu) blockers.push('Kaynak döneme karşılık hedef firmada uygun dönem yok.');
    return blockers;
  }, [
    selectedInvoiceKeys,
    selectedPoolRows.length,
    autoTargetLockError,
    targetSelectionRuleError,
    targetFirmaKodu,
    targetSubeKey,
    targetDepoKey,
    targetDonemKodu,
  ]);

  const lastTransfer = lastTransfers.length > 0 ? lastTransfers[lastTransfers.length - 1] : null;

  const lastTransferBase = useMemo(() => (lastTransfers.length > 0 ? lastTransfers : derivedLastTransfersFromLogs), [lastTransfers, derivedLastTransfersFromLogs]);
  const filteredLastTransfers = useMemo(() => {
    const contains = (a: any, b: any) =>
      (a ?? '').toString().toLowerCase().includes((b ?? '').toString().toLowerCase());
    const cariAdi = (filterCari ?? '').toString().trim();
    const faturaNo = (filterFaturaNo ?? '').toString().trim();
    const faturaTuru = (filterFaturaTuru ?? '').toString().trim();
    const kalemSube = (filterKalemSube ?? '').toString().trim();
    const durum = (filterDurum ?? '').toString().trim();
    const ustIslemQ = (ustIslemTuruFilter ?? '').toString().trim();

    let data = [...(lastTransferBase ?? [])];
    if (cariAdi) data = data.filter(t => contains(t.poolCariUnvan, cariAdi) || contains(t.poolCariKod, cariAdi));
    if (faturaNo) data = data.filter(t => contains(t.poolInvoiceNo, faturaNo) || contains(t.poolFisNo, faturaNo));
    if (faturaTuru) data = data.filter(_ => contains('FATURA', faturaTuru)); // Son aktarım hep fatura ekranı; placeholder
    if (kalemSube) data = data.filter(t => contains(t.poolKalemSube, kalemSube));
    if (ustIslemQ) data = data.filter(t => contains(t.poolUpperProcess, ustIslemQ));

    if (Number(sourceSubeKey) > 0) {
      const k = Number(sourceSubeKey);
      const name = String(allBranchNameByKeyRef.current?.[k] ?? '').trim();
      data = data.filter(t => Number(t.poolSourceSubeKey) === k || (name ? String(t.poolKaynakSube ?? '').trim() === name : false));
    }
    if (Number(sourceDepoKey) > 0) {
      const k = Number(sourceDepoKey);
      const name = String(allDepotNameByKeyRef.current?.[k] ?? '').trim();
      data = data.filter(t => Number(t.poolSourceDepoKey) === k || (name ? String(t.poolKaynakDepo ?? '').trim() === name : false));
    }
    if ((filterBaslangic ?? '').toString().trim()) {
      const b = parseFlexibleDateMs(filterBaslangic);
      if (!Number.isNaN(b)) data = data.filter(t => parseFlexibleDateMs(t.poolDate) >= b);
    }
    if ((filterBitis ?? '').toString().trim()) {
      const e = parseFlexibleDateMs(filterBitis);
      if (!Number.isNaN(e)) data = data.filter(t => parseFlexibleDateMs(t.poolDate) <= e);
    }
    if (durum) {
      if (durum === '0') data = data.filter(t => String(t.poolTransferStatus).toLowerCase().includes('bekliyor'));
      if (durum === '1') data = data.filter(t => String(t.poolTransferStatus).toLowerCase().includes('kısmi') || String(t.poolTransferStatus).toLowerCase().includes('kismi'));
      if (durum === '2') data = data.filter(t => String(t.poolTransferStatus).toLowerCase().includes('aktar'));
    }
    return data;
  }, [
    lastTransferBase,
    filterCari,
    filterFaturaNo,
    filterFaturaTuru,
    filterKalemSube,
    filterDurum,
    sourceSubeKey,
    sourceDepoKey,
    ustIslemTuruFilter,
    filterBaslangic,
    filterBitis,
  ]);

  const groupedLastTransfers = useMemo(() => {
    const map = new Map<number, any[]>();
    for (const t of filteredLastTransfers) {
      const invK = Number(t.poolSourceInvoiceKey);
      if (!Number.isFinite(invK) || invK <= 0) continue;
      const arr = map.get(invK) ?? [];
      arr.push(t);
      map.set(invK, arr);
    }
    const groups = Array.from(map.entries()).map(([invKey, items]) => {
      const first = items[items.length - 1] ?? items[0];

      // 1) Kayıtların içindeki (varsa) kalem listesi
      const storedLines = items.filter(x => Number(x.poolSourceLineKey) > 0);

      const enrichFromRpr = (ln: any) => {
        const lk = Number(ln.poolSourceLineKey);
        if (!Number.isFinite(lk) || lk <= 0) return ln;
        const r =
          allRows.find(r0 => Number(r0.invoiceKey) === invKey && Number(r0.lineKey) === lk && !r0.invalid)
          ?? allRows.find(r0 => Number(r0.invoiceKey) === invKey && Number(r0.lineKey) === lk);
        if (!r) {
          // RPR satırı bu session’da yoksa (farklı filtre/dönem), yine de gösterimi zengin tut.
          // NOT: Kalem Şube asla Üst İşlem'e fallback yapmaz.
          return {
            ...ln,
            poolKalemSube: String(ln.poolKalemSube ?? '').trim(),
            lineItemCode: String(ln.lineItemCode ?? '').trim(),
            lineItemName: String(ln.lineItemName ?? '').trim(),
          };
        }
        return {
          ...ln,
          poolSourceSubeKey: Number(ln.poolSourceSubeKey) || Number(r.sourceBranchKey) || 0,
          poolSourceDepoKey: Number(ln.poolSourceDepoKey) || Number(r.sourceWarehouseKey) || 0,
          poolKalemSube: String(ln.poolKalemSube ?? '').trim() || String(r.dynamicBranch ?? '').trim(),
          lineItemCode: String(ln.lineItemCode ?? '').trim() || String(r.itemCode ?? '').trim(),
          lineItemName: String(ln.lineItemName ?? '').trim() || String(r.itemName ?? '').trim(),
          lineUnitName: String(ln.lineUnitName ?? '').trim() || String(r.unitName ?? '').trim(),
          lineQuantity: Number.isFinite(Number(ln.lineQuantity)) ? Number(ln.lineQuantity) : Number(r.quantity ?? 0),
          lineTotal: Number.isFinite(Number(ln.lineTotal)) ? Number(ln.lineTotal) : Number(r.lineTotal ?? 0),
          poolDate: String(ln.poolDate ?? '').trim() || String(r.date ?? first.poolDate ?? '').trim(),
          poolFisNo: String(ln.poolFisNo ?? '').trim() || String(r.fisNo ?? first.poolFisNo ?? '').trim(),
          poolInvoiceNo: String(ln.poolInvoiceNo ?? '').trim() || String(r.invoiceNo ?? first.poolInvoiceNo ?? '').trim(),
          poolCariKod: String(ln.poolCariKod ?? '').trim() || String(r.cariCode ?? first.poolCariKod ?? '').trim(),
          poolCariUnvan: String(ln.poolCariUnvan ?? '').trim() || String(r.cariName ?? first.poolCariUnvan ?? '').trim(),
          poolUpperProcess: String(ln.poolUpperProcess ?? '').trim() || String(r.upperProcessName ?? r.upperProcessCode ?? first.poolUpperProcess ?? '').trim(),
          poolKaynakSube: String(ln.poolKaynakSube ?? '').trim() || String(allBranchNameByKeyRef.current?.[Number(r.sourceBranchKey)] ?? r.sourceBranchName ?? first.poolKaynakSube ?? '').trim(),
          poolKaynakDepo: String(ln.poolKaynakDepo ?? '').trim() || String(allDepotNameByKeyRef.current?.[Number(r.sourceWarehouseKey)] ?? r.sourceWarehouseName ?? first.poolKaynakDepo ?? '').trim(),
        };
      };

      // 2) Eski kayıtlar kalem saklamadıysa: RPR + transfer index ile faturanın aktarılan kalemlerini bul
      const historyLinesFromRpr =
        storedLines.length > 0
          ? []
          : allRows
            .filter(r => Number(r.invoiceKey) === invKey && !r.invalid)
            .filter(r => transferredLineIndexRef.current.has(`${invKey}:${Number(r.lineKey)}`))
            .map(r => ({
              ...first,
              poolSourceInvoiceKey: invKey,
              poolSourceLineKey: Number(r.lineKey) || 0,
              poolSourceSubeKey: Number(r.sourceBranchKey) || 0,
              poolSourceDepoKey: Number(r.sourceWarehouseKey) || 0,
              poolKalemSube: String(r.dynamicBranch ?? '').trim(),
              lineItemCode: String(r.itemCode ?? '').trim(),
              lineItemName: String(r.itemName ?? '').trim(),
              lineUnitName: String(r.unitName ?? '').trim(),
              lineQuantity: Number(r.quantity ?? 0),
              lineTotal: Number(r.lineTotal ?? 0),
              poolDate: String(r.date ?? first.poolDate ?? '').trim(),
              poolFisNo: String(r.fisNo ?? first.poolFisNo ?? '').trim(),
              poolInvoiceNo: String(r.invoiceNo ?? first.poolInvoiceNo ?? '').trim(),
              poolCariKod: String(r.cariCode ?? first.poolCariKod ?? '').trim(),
              poolCariUnvan: String(r.cariName ?? first.poolCariUnvan ?? '').trim(),
              poolDoviz: String(r.invoiceCurrencyCode ?? r.currencyCode ?? first.poolDoviz ?? '').trim(),
              poolToplam: Number.isFinite(Number(r.invoiceTotal)) ? Number(r.invoiceTotal) : first.poolToplam,
              poolIndirim: Number.isFinite(Number(r.invoiceDiscountTotal)) ? Number(r.invoiceDiscountTotal) : first.poolIndirim,
              poolMasraf: Number.isFinite(Number(r.invoiceExpenseTotal)) ? Number(r.invoiceExpenseTotal) : first.poolMasraf,
              poolKdv: Number.isFinite(Number(r.invoiceVat)) ? Number(r.invoiceVat) : first.poolKdv,
              poolNet: Number.isFinite(Number(r.invoiceNet)) ? Number(r.invoiceNet) : first.poolNet,
              poolKaynakSube: String(allBranchNameByKeyRef.current?.[Number(r.sourceBranchKey)] ?? r.sourceBranchName ?? first.poolKaynakSube ?? '').trim(),
              poolKaynakDepo: String(allDepotNameByKeyRef.current?.[Number(r.sourceWarehouseKey)] ?? r.sourceWarehouseName ?? first.poolKaynakDepo ?? '').trim(),
            }));

      const lines = [...storedLines, ...historyLinesFromRpr]
        .filter(x => Number(x.poolSourceLineKey) > 0)
        .map(enrichFromRpr);
      const branches = Array.from(new Set(lines.map(x => String(x.poolKalemSube || '').trim()).filter(Boolean)));
      return {
        invKey,
        header: first,
        lines: lines.sort((a, b) => Number(a.poolSourceLineKey) - Number(b.poolSourceLineKey)),
        branchSummary: branches.length > 0 ? branches.join(', ') : '',
        branchCount: branches.length,
        lineCount: lines.length,
      };
    });
    if (!invoiceSort.key) {
      // en yeni üstte: tarih + fallback
      return groups.sort((a, b) => String(b.header?.poolDate ?? '').localeCompare(String(a.header?.poolDate ?? '')));
    }
    const dirMul = invoiceSort.dir === 'asc' ? 1 : -1;
    const get = (g: any) => invoiceSort.key === 'invoiceNo'
      ? String(g?.header?.poolInvoiceNo ?? '').trim()
      : String(g?.header?.poolFisNo ?? '').trim();
    return groups.sort((a, b) => get(a).localeCompare(get(b), 'tr', { numeric: true, sensitivity: 'base' }) * dirMul);
  }, [filteredLastTransfers, allRows, invoiceSort.dir, invoiceSort.key]);

  // Aktarım: seçili kalemler → fatura grupları; doğrulama backend’de; HTTP dilimleri Promise.allSettled.

  // ── Aktarım ───────────────────────────────────────────────────────────────
  const handleTransfer = async () => {
    if (!canTransfer) return;
    setTransferring(true);
    setTransferAlert(null);
    setBulkTransferSummary(null);
    setTransferProgress(null);
    setFailedInvoices([]);
    try {
      const selectedRows = allRows.filter(r => selectedLineKeys.has(String(r.lineKey)));
      if (selectedRows.length === 0) {
        alert('Seçili kayıt yok');
        setTransferring(false);
        return;
      }
      // fingerprint skip (local registry)
      const targetFp = {
        targetFirmKey: String(targetFirmaKodu ?? ''),
        targetBranchKey: String(targetSubeKey ?? ''),
        targetWarehouseKey: String(targetDepoKey ?? ''),
        targetPeriodKey: String(targetDonemKodu ?? ''),
      };
      const rowsToTransfer = selectedRows.filter(r => {
        const fp = `${r.invoiceKey}:${r.lineKey}:${targetFp.targetFirmKey}:${targetFp.targetBranchKey}:${targetFp.targetWarehouseKey}:${targetFp.targetPeriodKey}`;
        if (transferRegistryRef.current.has(fp)) return false;
        return true;
      });

      const byInvoice = new Map<number, number[]>();
      for (const r of rowsToTransfer) {
        const invKey = Number(r.invoiceKey);
        if (!Number.isFinite(invKey) || invKey <= 0) continue; // cannot transfer without invoice key
        const lk = Number(r.lineKey);
        if (!Number.isFinite(lk) || lk <= 0) continue;
        const arr = byInvoice.get(invKey) ?? [];
        arr.push(lk);
        byInvoice.set(invKey, arr);
      }

      const selectedRowsByInvoice = new Map<number, NormalizedRprRow[]>();
      for (const r of rowsToTransfer) {
        const inv = Number(r.invoiceKey);
        if (!Number.isFinite(inv) || inv <= 0) continue;
        const arr = selectedRowsByInvoice.get(inv) ?? [];
        arr.push(r);
        selectedRowsByInvoice.set(inv, arr);
      }

      const invoicePayloads = Array.from(byInvoice.entries()).map(([sourceInvoiceKey, keys]) => {
        const rows = selectedRowsByInvoice.get(sourceInvoiceKey) ?? [];
        const rowsForSnap =
          rows.length > 0
            ? rows
            : allRows.filter(r => Number(r.invoiceKey) === sourceInvoiceKey);
        const lineKeys =
          rowsForSnap.length > 0
            ? Array.from(new Set(rowsForSnap.map(r => Number(r.lineKey)).filter(k => Number.isFinite(k) && k > 0)))
            : Array.from(new Set(keys));
        return {
          sourceInvoiceKey,
          selectedKalemKeys: lineKeys,
          useDynamicBranch: isDistributableMode,
          headerSnapshot: buildHeaderSnapshotFromInvoice(allRows, sourceInvoiceKey),
          selectedLineSnapshots: rowsForSnap.map(r => ({
            sourceLineKey: Number(r.lineKey) || undefined,
            itemCode: String(r.itemCode ?? ''),
            stokKartKodu: String(r.itemCode ?? ''),
            lineTypeLabel: String(r.lineTypeLabel ?? ''),
            dynamicBranch: String(r.dynamicBranch ?? ''),
            aciklama: String(r.itemName ?? ''),
            birimAdi: String(r.unitName ?? ''),
            miktar: Number(r.quantity ?? 0),
            birimFiyati: Number(r.unitPrice ?? 0),
            lineCurrencyCode: String(r.currencyCode ?? '').trim(),
            yerelBirimFiyati: Number(r.yerelBirimFiyati ?? r.unitPrice ?? 0),
            sonBirimFiyati: Number(r.sonBirimFiyati ?? r.unitPrice ?? 0),
            tutar: Number(r.lineTotal ?? 0),
            indirim: Number(r.lineDiscount ?? 0),
            masraf: Number(r.lineExpense ?? 0),
            kdvYuzde: Number(r.lineKdvPercent ?? 0),
            kdvTutari: Number(r.lineKdvTutari ?? 0),
          })),
        };
      });

      if (invoicePayloads.length === 0) {
        setTransferAlert('Aktarılacak fatura kalmadı (tüm kalemler bu hedefe daha önce aktarılmış olabilir).');
        setTransferring(false);
        return;
      }

      // Sunucu TransferRawMode + toplu aktarım paralellik ipuçları (DiaSettings).
      let rawModeForTransfer = transferFlags.transferRawMode;
      let httpParallel = 4;
      let maxInvoicesPerHttp = 12;
      try {
        const f = await InvoiceService.getTransferFlags();
        rawModeForTransfer = Boolean(f.transferRawMode);
        if (Number.isFinite(f.transferConcurrency) && (f.transferConcurrency ?? 0) > 0)
          httpParallel = Math.min(32, Math.max(2, Number(f.transferConcurrency)));
        if (Number.isFinite(f.transferBatchSize) && (f.transferBatchSize ?? 0) > 0)
          maxInvoicesPerHttp = Math.min(100, Math.max(2, Number(f.transferBatchSize)));
        setTransferFlags(prev => ({
          ...prev,
          transferRawMode: rawModeForTransfer,
          transferConcurrency: f.transferConcurrency ?? prev.transferConcurrency,
          transferBatchSize: f.transferBatchSize ?? prev.transferBatchSize,
        }));
      } catch {
        /* transfer-flags alınamazsa önceki state */
      }

      const payloadsForNetwork = await enrichPayloadsForRawMode(
        invoicePayloads,
        rawModeForTransfer,
        Number(targetFirmaKodu),
        Number(targetDonemKodu) || 0
      );

      cancelRequestedRef.current = false;
      abortRef.current?.abort();
      abortRef.current = new AbortController();
      const signal = abortRef.current.signal;

      const transferReqBase = {
        sourceFirmaKodu: poolFirmaKodu,
        sourceDonemKodu: sourceDonemKodu,
        sourceSubeKey: sourceSubeKey,
        sourceDepoKey: sourceDepoKey,
        targetFirmaKodu: Number(targetFirmaKodu),
        targetDonemKodu: Number(targetDonemKodu) || 0,
        targetSubeKey: Number(targetSubeKey) || 0,
        targetDepoKey: Number(targetDepoKey) || 0,
      };

      const httpPayloadChunks: InvoiceTransferPayload[][] = [];
      for (let i = 0; i < payloadsForNetwork.length; i += maxInvoicesPerHttp) {
        httpPayloadChunks.push(payloadsForNetwork.slice(i, i + maxInvoicesPerHttp));
      }

      pushLog({
        source_kalem_key: '',
        status: 'success',
        message: `Aktarım: ${payloadsForNetwork.length} fatura → istek başına en çok ${maxInvoicesPerHttp} fatura, eşzamanlı en çok ${httpParallel} HTTP; sunucu içi paralellik DiaSettings:TransferConcurrency.`,
        was_duplicate_override: false,
      });
      setTransferProgress({
        total: invoicePayloads.length,
        success: 0,
        failed: 0,
        remaining: payloadsForNetwork.length,
        inFlight: Math.min(httpParallel, httpPayloadChunks.length),
      });

      if (cancelRequestedRef.current || signal.aborted) throw new Error('İptal edildi');

      const resultByKey = new Map<number, { sourceInvoiceKey: number; result: any }>();
      let processedInvoices = 0;

      for (let waveStart = 0; waveStart < httpPayloadChunks.length; waveStart += httpParallel) {
        if (cancelRequestedRef.current || signal.aborted) throw new Error('İptal edildi');
        const wave = httpPayloadChunks.slice(waveStart, waveStart + httpParallel);
        const waveSettled = await Promise.allSettled(
          wave.map(chunk => InvoiceService.faturaAktar({ ...transferReqBase, invoices: chunk } as any, { signal }))
        );

        for (let wi = 0; wi < waveSettled.length; wi++) {
          const s = waveSettled[wi];
          const invChunk = wave[wi];
          if (s.status === 'fulfilled') {
            const br = s.value;
            const rows = (br.results ?? []) as Array<{ sourceInvoiceKey: number; result: any }>;
            const seen = new Set<number>();
            for (const row of rows) {
              const k = Number(row?.sourceInvoiceKey ?? 0);
              if (Number.isFinite(k) && k > 0) {
                seen.add(k);
                resultByKey.set(k, row);
              }
            }
            for (const p of invChunk) {
              const k = Number(p.sourceInvoiceKey);
              if (!seen.has(k)) {
                resultByKey.set(k, {
                  sourceInvoiceKey: k,
                  result: {
                    success: false,
                    message: 'Sunucu sonucu eksik.',
                    failureCode: 'missing_result',
                  },
                });
              }
            }
          } else {
            const reason = s.reason as {
              name?: string;
              code?: string;
              response?: { data?: { message?: string } };
              message?: string;
            };
            const msg =
              reason?.name === 'CanceledError' || reason?.code === 'ERR_CANCELED'
                ? 'İptal edildi'
                : (reason?.response?.data?.message ?? reason?.message ?? String(reason));
            for (const p of invChunk) {
              const k = Number(p.sourceInvoiceKey);
              resultByKey.set(k, {
                sourceInvoiceKey: k,
                result: { success: false, message: msg, failureCode: 'network' },
              });
            }
          }
        }

        processedInvoices += wave.reduce((acc, c) => acc + c.length, 0);
        const pendingHttpChunks = httpPayloadChunks.length - waveStart - wave.length;
        setTransferProgress(p =>
          p
            ? {
                ...p,
                remaining: Math.max(0, payloadsForNetwork.length - processedInvoices),
                inFlight:
                  pendingHttpChunks > 0 ? Math.min(httpParallel, pendingHttpChunks) : 0,
              }
            : p
        );
      }

      const resultsBag = payloadsForNetwork.map(p => {
        const k = Number(p.sourceInvoiceKey);
        return (
          resultByKey.get(k) ?? {
            sourceInvoiceKey: k,
            result: { success: false, message: 'Sonuç birleştirilemedi.', failureCode: 'missing_result' },
          }
        );
      });

      const okFinal = resultsBag.filter(x => x?.result?.success === true).length;
      const failFinal = resultsBag.filter(x => x?.result?.success !== true).length;
      setTransferProgress(p => p ? ({ ...p, success: okFinal, failed: failFinal, remaining: 0, inFlight: 0 }) : p);

      const failedBag = invoicePayloads
        .filter(invP => {
          const hit = resultsBag.find(r => Number(r.sourceInvoiceKey) === Number(invP.sourceInvoiceKey));
          return !hit || hit.result?.success !== true;
        })
        .map(invP => ({ sourceInvoiceKey: invP.sourceInvoiceKey, selectedKalemKeys: invP.selectedKalemKeys }));
      setFailedInvoices(failedBag);

      const bulk = { results: resultsBag };

      const transferredLineByInvoice = new Map<number, number[]>();
      let lastCreatedForViewer = { key: 0, firma: 0, donem: 0 };

      for (const item of (bulk?.results ?? [])) {
        const invKey = Number(item?.sourceInvoiceKey ?? 0);
        const res: any = item?.result;
        const errParts = Array.isArray(res?.errors) ? res.errors.filter(Boolean).join(' · ') : '';
        const failMeta = [res?.failureCode && `code=${res.failureCode}`, res?.failureStage && `stage=${res.failureStage}`]
          .filter(Boolean)
          .join(' ');
        const failMessage =
          res?.message
          ?? (errParts || undefined)
          ?? 'Aktarım tamamlanamadı.';
        const failMessageFull = failMeta ? `${failMessage} (${failMeta})` : failMessage;

        if (res?.success === false || res?.createdInvoiceKey == null || Number(res.createdInvoiceKey) <= 0) {
          pushLog({ source_kalem_key: '', status: 'error', message: `Aktarım başarısız: invoiceKey=${invKey} | ${failMessageFull}`, was_duplicate_override: false });
          pushTransferLogItem({
            invoiceKey: invKey,
            lineCount: Number(invoicePayloads.find(p => Number(p.sourceInvoiceKey) === invKey)?.selectedKalemKeys?.length ?? 0),
            targetFirma: Number(targetFirmaKodu) || 0,
            success: false,
            errorMessage: failMessageFull,
          });
          continue;
        }

        // success -> add fingerprints for lines that actually transferred
        const sentLineKeys = invoicePayloads.find(p => Number(p.sourceInvoiceKey) === invKey)?.selectedKalemKeys ?? [];
        const transferredKeysFromServer = Array.isArray(res?.transferredSourceKalemKeys)
          ? (res.transferredSourceKalemKeys as any[]).map(Number).filter(n => Number.isFinite(n) && n > 0)
          : [];
        const transferredNow = transferredKeysFromServer.length > 0 ? transferredKeysFromServer : sentLineKeys.map(Number);
        transferredLineByInvoice.set(invKey, transferredNow);
        for (const lk of transferredNow) {
          const fp = `${invKey}:${lk}:${targetFp.targetFirmKey}:${targetFp.targetBranchKey}:${targetFp.targetWarehouseKey}:${targetFp.targetPeriodKey}`;
          transferRegistryRef.current.add(fp);
        }
        persistTransferRegistry();
        pushTransferLogItem({
          invoiceKey: invKey,
          lineCount: Number(invoicePayloads.find(p => Number(p.sourceInvoiceKey) === invKey)?.selectedKalemKeys?.length ?? 0),
          targetFirma: Number(targetFirmaKodu) || 0,
          success: true,
        });

        const verified = Boolean(res?.createdVerified);
        const invPack = invoicePayloads.find(p => Number(p.sourceInvoiceKey) === invKey);
        const hs = invPack?.headerSnapshot as Record<string, unknown> | undefined;
        const { meta: poolMeta, cariRow: poolCariRow } = pickInvoiceRowsForTransferDisplay(allRows, invKey);
        const poolCariKod = pickPoolCariKodFromTransfer(hs, poolCariRow);
        const invTransferLabel =
          computeInvoiceTransferStatus(sentLineKeys.length || transferredNow.length, transferredNow.length) ||
          'Aktarıldı';

        const modeRows = (selectedRowsByInvoice.get(invKey) ?? []).filter(x => !x.invalid);
        const buildEntry = (rowForGrid: NormalizedRprRow | undefined, sourceLineKey?: number) => {
          const poolInvoiceNo = String(hs?.invoiceNo ?? rowForGrid?.invoiceNo ?? poolMeta?.invoiceNo ?? '').trim();
          const poolFisNo = String(hs?.fisNo ?? rowForGrid?.fisNo ?? poolMeta?.fisNo ?? '').trim();
          const poolDate = String(hs?.date ?? rowForGrid?.date ?? poolMeta?.date ?? '').trim();
          const poolCariUnvan = String(hs?.cariName ?? rowForGrid?.cariName ?? poolMeta?.cariName ?? '').trim();
          const poolNetVal = pickPoolNetForLastTransfer(hs, rowForGrid ?? poolMeta);
          const kaynakSubeResolved =
            String(allBranchNameByKeyRef.current?.[Number((rowForGrid ?? poolMeta)?.sourceBranchKey)] ?? '').trim() ||
            String((rowForGrid ?? poolMeta)?.sourceBranchName ?? '').trim();
          const kaynakDepoResolved =
            String(allDepotNameByKeyRef.current?.[Number((rowForGrid ?? poolMeta)?.sourceWarehouseKey)] ?? '').trim() ||
            String((rowForGrid ?? poolMeta)?.sourceWarehouseName ?? '').trim();
          return {
            createdInvoiceKey: Number(res.createdInvoiceKey),
            targetFirmaKodu: Number(targetFirmaKodu),
            targetFirmaAdi: targetFirmaAdi,
            targetDonemKodu: Number(targetDonemKodu),
            targetSubeKey: Number(targetSubeKey),
            targetDepoKey: Number(targetDepoKey),
            ...(poolInvoiceNo ? { poolInvoiceNo } : {}),
            ...(poolFisNo ? { poolFisNo } : {}),
            ...(poolDate ? { poolDate } : {}),
            ...(poolCariKod ? { poolCariKod } : {}),
            ...(poolCariUnvan ? { poolCariUnvan } : {}),
            ...(poolNetVal != null && Number.isFinite(poolNetVal) ? { poolNet: poolNetVal } : {}),
            poolSourceInvoiceKey: invKey,
            ...(sourceLineKey && sourceLineKey > 0 ? { poolSourceLineKey: sourceLineKey } : {}),
            poolSourceSubeKey: Number((rowForGrid ?? poolMeta)?.sourceBranchKey) || 0,
            poolSourceDepoKey: Number((rowForGrid ?? poolMeta)?.sourceWarehouseKey) || 0,
            ...(rowForGrid?.itemCode ? { lineItemCode: String(rowForGrid.itemCode) } : {}),
            ...(rowForGrid?.itemName ? { lineItemName: String(rowForGrid.itemName) } : {}),
            ...(rowForGrid?.unitName ? { lineUnitName: String(rowForGrid.unitName) } : {}),
            ...(rowForGrid?.quantity != null ? { lineQuantity: Number(rowForGrid.quantity) } : {}),
            ...(rowForGrid?.lineTotal != null ? { lineTotal: Number(rowForGrid.lineTotal) } : {}),
            ...poolMetaToLastTransferGrid(rowForGrid ?? poolMeta),
            ...(kaynakSubeResolved ? { poolKaynakSube: kaynakSubeResolved } : {}),
            ...(kaynakDepoResolved ? { poolKaynakDepo: kaynakDepoResolved } : {}),
            poolTransferStatus: invTransferLabel,
          };
        };

        const entries =
          isDistributableMode
            ? transferredNow.map(lk => {
              const rowForGrid =
                modeRows.find(r => Number(r.lineKey) === Number(lk))
                ?? allRows.find(r => Number(r.invoiceKey) === invKey && Number(r.lineKey) === Number(lk) && !r.invalid);
              return buildEntry(rowForGrid, Number(lk));
            })
            : [buildEntry(poolMeta)];

        setLastTransfers(prev => {
          const next = [...prev, ...entries].slice(-50);
          try { localStorage.setItem(LAST_TRANSFERS_KEY, JSON.stringify(next.slice(-50))); } catch {}
          return next;
        });

        lastCreatedForViewer = {
          key: Number(res.createdInvoiceKey),
          firma: Number(targetFirmaKodu),
          donem: Number(targetDonemKodu) || 0,
        };

        pushLog({
          source_kalem_key: '',
          status: verified ? 'success' : 'error',
          message: `${res.message ?? 'Aktarım'} (key=${res.createdInvoiceKey}) | ${verified ? 'doğrulandı' : 'doğrulanamadı'}`,
          was_duplicate_override: false,
        });
        if (!verified) {
          setTransferAlert(res?.message ?? 'Kayıt oluşturuldu ancak doğrulama başarısız.');
        }

        if ((res.duplicateSkippedCount ?? 0) > 0) {
          pushLog({
            source_kalem_key: '',
            status: 'duplicate',
            message: `${res.duplicateSkippedCount} kalem duplicate nedeniyle atlandı.`,
            was_duplicate_override: false,
          });
        }

        const skipped = Array.isArray((res as any)?.skippedExtraFields) ? ((res as any).skippedExtraFields as any[]) : [];
        if (skipped.length > 0) {
          const shown = skipped
            .slice(0, 8)
            .map(x => `${String(x?.scope ?? 'line')}.${String(x?.name ?? '').trim()} (${String(x?.reason ?? '').trim() || 'skip'})`)
            .filter(s => s.length > 10);
          pushLog({
            source_kalem_key: '',
            status: 'duplicate',
            message: `Uyarı: ${skipped.length} ek alan aktarılamadı (object/array veya engelli alan). İlkleri: ${shown.join(' · ')}`,
            was_duplicate_override: false,
          });
        }

        // Not: kalan satırları 'Bekliyor'ya zorlamıyoruz; havuzun gerçek transfer_status alanı korunur.
      }

      if (transferredLineByInvoice.size > 0) {
        setAllRows(prev =>
          prev.map(r => {
            const lk = transferredLineByInvoice.get(Number(r.invoiceKey));
            if (!lk?.includes(Number(r.lineKey))) return r;
            return { ...r, transferStatus: 'Aktarıldı' };
          })
        );
        setFilteredRows(prev =>
          prev.map(r => {
            const lk = transferredLineByInvoice.get(Number(r.invoiceKey));
            if (!lk?.includes(Number(r.lineKey))) return r;
            return { ...r, transferStatus: 'Aktarıldı' };
          })
        );
      }

      if (lastCreatedForViewer.key > 0) {
        setActiveTab('last');
      }

      // UI summary
      const results = (bulk?.results ?? []).map(x => x?.result);
      const ok = results.filter((r: any) => r?.success === true).length;
      const errorRows = results.filter((r: any) => r?.success !== true);
      const error = errorRows.length;
      const messages = errorRows
        .map((r: any) => (r?.message ?? '').toString().trim())
        .filter((m: string) => m.length > 0)
        .slice(0, 20);
      setBulkTransferSummary({ ok, error, messages });

      if (ok > 0 && error > 0) {
        setTransferAlert(`Paralel aktarım: ${ok} başarılı, ${error} hata (ayrıntı günlükte).`);
      } else if (error > 0 && ok === 0) {
        setTransferAlert(`Aktarım tamamlanamadı: ${error} fatura hata. İlk mesajlar özet kutusunda.`);
      }

      // Son aktarılan: havuz bilgisi satıra yazılır; hedef özet için son başarılı fatura listelenir.
    } catch (err: any) {
      const data = err?.response?.data;
      const msg = data?.message ?? err?.message ?? 'Aktarım başarısız.';
      const details = data?.details ? ` | ${data.details}` : '';
      const trace = data?.traceId ? ` | traceId=${data.traceId}` : '';
      setTransferAlert(msg);
      pushLog({ source_kalem_key: '', status: 'error', message: `${msg}${details}${trace}`, was_duplicate_override: false });
      console.error('[Transfer] full error', err?.response?.data ?? err);
    } finally {
      setTransferring(false);
    }
  };

  const cancelTransfer = useCallback(() => {
    cancelRequestedRef.current = true;
    try { abortRef.current?.abort(); } catch {}
    setTransferAlert('Aktarım iptal edildi.');
    setTransferring(false);
    setTransferProgress(p => p ? ({ ...p, inFlight: 0 }) : null);
  }, []);

  const retryFailed = useCallback(async () => {
    if (failedInvoices.length === 0) return;
    cancelRequestedRef.current = false;
    abortRef.current?.abort();
    abortRef.current = new AbortController();
    const signal = abortRef.current.signal;

    const rebuilt = failedInvoices.map(f => {
      const rows = allRows.filter(r =>
        Number(r.invoiceKey) === Number(f.sourceInvoiceKey) &&
        f.selectedKalemKeys.includes(Number(r.lineKey))
      );
      const rowsForSnap =
        rows.length > 0
          ? rows
          : allRows.filter(r => Number(r.invoiceKey) === Number(f.sourceInvoiceKey));
      return {
        sourceInvoiceKey: f.sourceInvoiceKey,
        selectedKalemKeys: Array.from(new Set(rowsForSnap.map(r => Number(r.lineKey)).filter(k => k > 0))),
        headerSnapshot: buildHeaderSnapshotFromInvoice(allRows, Number(f.sourceInvoiceKey)),
        selectedLineSnapshots: rowsForSnap.map(r => ({
          sourceLineKey: Number(r.lineKey) || undefined,
          itemCode: String(r.itemCode ?? ''),
          stokKartKodu: String(r.itemCode ?? ''),
          lineTypeLabel: String(r.lineTypeLabel ?? ''),
          dynamicBranch: String(r.dynamicBranch ?? ''),
          aciklama: String(r.itemName ?? ''),
          birimAdi: String(r.unitName ?? ''),
          miktar: Number(r.quantity ?? 0),
          birimFiyati: Number(r.unitPrice ?? 0),
          lineCurrencyCode: String(r.currencyCode ?? '').trim(),
          yerelBirimFiyati: Number(r.yerelBirimFiyati ?? r.unitPrice ?? 0),
          sonBirimFiyati: Number(r.sonBirimFiyati ?? r.unitPrice ?? 0),
          tutar: Number(r.lineTotal ?? 0),
          indirim: Number(r.lineDiscount ?? 0),
          masraf: Number(r.lineExpense ?? 0),
          kdvYuzde: Number(r.lineKdvPercent ?? 0),
          kdvTutari: Number(r.lineKdvTutari ?? 0),
        })),
      };
    }).filter(p => p.selectedKalemKeys.length > 0);

    if (rebuilt.length === 0) {
      setTransferAlert('Tekrar denenecek seçili kalem bulunamadı (liste yenilendi mi?).');
      return;
    }

    for (const p of rebuilt) {
      const headerOk = p.headerSnapshot != null && typeof p.headerSnapshot === 'object';
      const linesOk = Array.isArray(p.selectedLineSnapshots) && p.selectedLineSnapshots.length > 0;
      if (!headerOk || !linesOk) {
        // eslint-disable-next-line no-console
        console.error('SNAPSHOT HATALI (retry)', p.sourceInvoiceKey, { headerOk, lineCount: p.selectedLineSnapshots?.length ?? 0 });
        setTransferAlert(`SNAPSHOT eksik (fatura ${p.sourceInvoiceKey}). Grid verisi güncel mi?`);
        return;
      }
      const hs = p.headerSnapshot as Record<string, unknown>;
      const cari = String(hs?.cariCode ?? '').trim();
      const date = String(hs?.date ?? '').trim();
      const itc = Number(hs?.invoiceTypeCode);
      const cc = String(hs?.currencyCode ?? '').trim();
      if (!cari || !date || !Number.isFinite(itc) || itc <= 0 || !cc) {
        setTransferAlert(`SNAPSHOT başlık eksik (fatura ${p.sourceInvoiceKey}).`);
        return;
      }
      for (const ln of p.selectedLineSnapshots ?? []) {
        const code = String((ln as { itemCode?: string })?.itemCode ?? (ln as { stokKartKodu?: string })?.stokKartKodu ?? '').trim();
        const mq = Number((ln as { miktar?: number })?.miktar);
        const bf = (ln as { birimFiyati?: number })?.birimFiyati;
        const kdv = (ln as { kdvYuzde?: number })?.kdvYuzde;
        if (!code || !Number.isFinite(mq) || mq <= 0 || bf === undefined || bf === null || Number.isNaN(Number(bf)) || kdv === undefined || kdv === null) {
          setTransferAlert(`SNAPSHOT satır eksik (fatura ${p.sourceInvoiceKey}).`);
          return;
        }
      }
    }

    let rawModeForRetry = transferFlags.transferRawMode;
    try {
      const f = await InvoiceService.getTransferFlags();
      rawModeForRetry = Boolean(f.transferRawMode);
      setTransferFlags(prev => ({ ...prev, transferRawMode: rawModeForRetry }));
    } catch {
      /* önceki state */
    }

    let enrichedRebuilt: InvoiceTransferPayload[];
    try {
      enrichedRebuilt = await enrichPayloadsForRawMode(
        rebuilt,
        rawModeForRetry,
        Number(targetFirmaKodu),
        Number(targetDonemKodu) || 0
      );
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'RAW başlık çözümlemesi başarısız.';
      setTransferAlert(msg);
      pushLog({ source_kalem_key: '', status: 'error', message: msg, was_duplicate_override: false });
      return;
    }

    setTransferring(true);
    setTransferAlert(null);
    setTransferProgress({ total: enrichedRebuilt.length, success: 0, failed: 0, remaining: enrichedRebuilt.length, inFlight: 1 });

    try {
      const bulkRes = await InvoiceService.faturaAktar({
        sourceFirmaKodu: poolFirmaKodu,
        sourceDonemKodu: sourceDonemKodu,
        sourceSubeKey: sourceSubeKey,
        sourceDepoKey: sourceDepoKey,
        targetFirmaKodu: Number(targetFirmaKodu),
        targetDonemKodu: Number(targetDonemKodu) || 0,
        targetSubeKey: Number(targetSubeKey) || 0,
        targetDepoKey: Number(targetDepoKey) || 0,
        invoices: enrichedRebuilt,
      } as any, { signal });

      let resultsBag = ([...(bulkRes.results ?? [])] as Array<{ sourceInvoiceKey: number; result: any }>);
      for (const invP of enrichedRebuilt) {
        if (!resultsBag.some(r => Number(r.sourceInvoiceKey) === Number(invP.sourceInvoiceKey))) {
          resultsBag.push({
            sourceInvoiceKey: invP.sourceInvoiceKey,
            result: { success: false, message: 'Sunucu sonucu eksik.', failureCode: 'missing_result' },
          });
        }
      }
      const okFinal = resultsBag.filter(x => x?.result?.success === true).length;
      const failFinal = resultsBag.filter(x => x?.result?.success !== true).length;
      setTransferProgress(p => p ? ({ ...p, success: okFinal, failed: failFinal, remaining: 0, inFlight: 0 }) : p);

      const failedBag = enrichedRebuilt
        .filter(invP => {
          const hit = resultsBag.find(r => Number(r.sourceInvoiceKey) === Number(invP.sourceInvoiceKey));
          return !hit || hit.result?.success !== true;
        })
        .map(invP => ({ sourceInvoiceKey: invP.sourceInvoiceKey, selectedKalemKeys: invP.selectedKalemKeys }));
      setFailedInvoices(failedBag);

      const targetFpRetry = {
        targetFirmKey: String(targetFirmaKodu ?? ''),
        targetBranchKey: String(targetSubeKey ?? ''),
        targetWarehouseKey: String(targetDepoKey ?? ''),
        targetPeriodKey: String(targetDonemKodu ?? ''),
      };
      const okRetry = resultsBag.filter(
        x => x?.result?.success === true && Number((x.result as any)?.createdInvoiceKey) > 0
      );
      const linesByInvRetry = new Map<number, number[]>();
      let lastCreatedRetry = { key: 0, firma: 0, donem: 0 };
      for (const it of okRetry) {
        const invK = Number(it.sourceInvoiceKey);
        const res = it.result as any;
        const keys = enrichedRebuilt.find(p => Number(p.sourceInvoiceKey) === invK)?.selectedKalemKeys ?? [];
        const transferredKeysFromServer = Array.isArray(res?.transferredSourceKalemKeys)
          ? (res.transferredSourceKalemKeys as any[]).map(Number).filter(n => Number.isFinite(n) && n > 0)
          : [];
        const transferredNow = transferredKeysFromServer.length > 0 ? transferredKeysFromServer : keys.map(Number);
        linesByInvRetry.set(invK, transferredNow);
        for (const lk of transferredNow) {
          const fp = `${invK}:${lk}:${targetFpRetry.targetFirmKey}:${targetFpRetry.targetBranchKey}:${targetFpRetry.targetWarehouseKey}:${targetFpRetry.targetPeriodKey}`;
          transferRegistryRef.current.add(fp);
        }
        const invPack = enrichedRebuilt.find(p => Number(p.sourceInvoiceKey) === invK);
        const hs = invPack?.headerSnapshot as Record<string, unknown> | undefined;
        const { meta: poolMeta, cariRow: poolCariRow } = pickInvoiceRowsForTransferDisplay(allRows, invK);
        const poolCariKod = pickPoolCariKodFromTransfer(hs, poolCariRow);
        const invTransferLabel =
          computeInvoiceTransferStatus(keys.length || transferredNow.length, transferredNow.length) ||
          'Aktarıldı';

        const modeRows = allRows.filter(r => Number(r.invoiceKey) === invK && !r.invalid);
        const buildEntry = (rowForGrid: NormalizedRprRow | undefined, sourceLineKey?: number) => {
          const poolInvoiceNo = String(hs?.invoiceNo ?? rowForGrid?.invoiceNo ?? poolMeta?.invoiceNo ?? '').trim();
          const poolFisNo = String(hs?.fisNo ?? rowForGrid?.fisNo ?? poolMeta?.fisNo ?? '').trim();
          const poolDate = String(hs?.date ?? rowForGrid?.date ?? poolMeta?.date ?? '').trim();
          const poolCariUnvan = String(hs?.cariName ?? rowForGrid?.cariName ?? poolMeta?.cariName ?? '').trim();
          const poolNetVal = pickPoolNetForLastTransfer(hs, rowForGrid ?? poolMeta);
          const kaynakSubeResolved =
            String(allBranchNameByKeyRef.current?.[Number((rowForGrid ?? poolMeta)?.sourceBranchKey)] ?? '').trim() ||
            String((rowForGrid ?? poolMeta)?.sourceBranchName ?? '').trim();
          const kaynakDepoResolved =
            String(allDepotNameByKeyRef.current?.[Number((rowForGrid ?? poolMeta)?.sourceWarehouseKey)] ?? '').trim() ||
            String((rowForGrid ?? poolMeta)?.sourceWarehouseName ?? '').trim();
          return {
            createdInvoiceKey: Number(res.createdInvoiceKey),
            targetFirmaKodu: Number(targetFirmaKodu),
            targetFirmaAdi: targetFirmaAdi,
            targetDonemKodu: Number(targetDonemKodu) || 0,
            targetSubeKey: Number(targetSubeKey) || 0,
            targetDepoKey: Number(targetDepoKey) || 0,
            ...(poolInvoiceNo ? { poolInvoiceNo } : {}),
            ...(poolFisNo ? { poolFisNo } : {}),
            ...(poolDate ? { poolDate } : {}),
            ...(poolCariKod ? { poolCariKod } : {}),
            ...(poolCariUnvan ? { poolCariUnvan } : {}),
            ...(poolNetVal != null && Number.isFinite(poolNetVal) ? { poolNet: poolNetVal } : {}),
            poolSourceInvoiceKey: invK,
            ...(sourceLineKey && sourceLineKey > 0 ? { poolSourceLineKey: sourceLineKey } : {}),
            poolSourceSubeKey: Number((rowForGrid ?? poolMeta)?.sourceBranchKey) || 0,
            poolSourceDepoKey: Number((rowForGrid ?? poolMeta)?.sourceWarehouseKey) || 0,
            ...(rowForGrid?.itemCode ? { lineItemCode: String(rowForGrid.itemCode) } : {}),
            ...(rowForGrid?.itemName ? { lineItemName: String(rowForGrid.itemName) } : {}),
            ...(rowForGrid?.unitName ? { lineUnitName: String(rowForGrid.unitName) } : {}),
            ...(rowForGrid?.quantity != null ? { lineQuantity: Number(rowForGrid.quantity) } : {}),
            ...(rowForGrid?.lineTotal != null ? { lineTotal: Number(rowForGrid.lineTotal) } : {}),
            ...poolMetaToLastTransferGrid(rowForGrid ?? poolMeta),
            ...(kaynakSubeResolved ? { poolKaynakSube: kaynakSubeResolved } : {}),
            ...(kaynakDepoResolved ? { poolKaynakDepo: kaynakDepoResolved } : {}),
            poolTransferStatus: invTransferLabel,
          };
        };

        const entries =
          isDistributableMode
            ? transferredNow.map(lk => {
              const rowForGrid =
                modeRows.find(r => Number(r.lineKey) === Number(lk))
                ?? allRows.find(r => Number(r.invoiceKey) === invK && Number(r.lineKey) === Number(lk) && !r.invalid);
              return buildEntry(rowForGrid, Number(lk));
            })
            : [buildEntry(poolMeta)];

        setLastTransfers(prev => {
          const next = [...prev, ...entries].slice(-50);
          try { localStorage.setItem(LAST_TRANSFERS_KEY, JSON.stringify(next.slice(-50))); } catch {}
          return next;
        });
        lastCreatedRetry = {
          key: Number(res.createdInvoiceKey),
          firma: Number(targetFirmaKodu),
          donem: Number(targetDonemKodu) || 0,
        };
      }
      if (okRetry.length > 0) persistTransferRegistry();
      if (linesByInvRetry.size > 0) {
        setAllRows(prev =>
          prev.map(r => {
            const lk = linesByInvRetry.get(Number(r.invoiceKey));
            if (!lk?.includes(Number(r.lineKey))) return r;
            return { ...r, transferStatus: 'Aktarıldı' };
          })
        );
        setFilteredRows(prev =>
          prev.map(r => {
            const lk = linesByInvRetry.get(Number(r.invoiceKey));
            if (!lk?.includes(Number(r.lineKey))) return r;
            return { ...r, transferStatus: 'Aktarıldı' };
          })
        );
      }
      if (lastCreatedRetry.key > 0) {
        setActiveTab('last');
      }

      pushLog({
        source_kalem_key: '',
        status: failFinal > 0 ? 'error' : 'success',
        message: `Tekrar dene tamamlandı: ok=${okFinal} fail=${failFinal}`,
        was_duplicate_override: false,
      });
    } catch (e: any) {
      const msg = e?.response?.data?.message ?? e?.message ?? 'Tekrar dene başarısız.';
      setTransferAlert(msg);
      pushLog({ source_kalem_key: '', status: 'error', message: msg, was_duplicate_override: false });
    } finally {
      setTransferring(false);
    }
  }, [
    failedInvoices,
    allRows,
    poolFirmaKodu,
    sourceDonemKodu,
    sourceSubeKey,
    sourceDepoKey,
    targetFirmaKodu,
    targetDonemKodu,
    targetSubeKey,
    targetDepoKey,
    targetFirmaAdi,
    transferFlags.transferRawMode,
  ]);

  // (Özel Rapor paneli kaldırıldı; aktarım akışı handleTransfer üzerinden yürür.)

  const resetFilters = () => {
    setFilterFaturaNo('');
    setFilterCari('');
    setFilterFaturaTuru('');
    setFilterDurum('');
    setFilterKalemSube('');
    setFilterBaslangic('');
    setFilterBitis('');
    setTransferTypeFilter('tum_faturalar');
    resetSelectionState(true);
  };

  const handleRefresh = () => {
    setLoading(true);
    setLinesLoading(false);
    setLookupError(null);
    setTransferAlert(null);
    resetSelectionState(true);
    setFilterFaturaNo('');
    setFilterCari('');
    setFilterFaturaTuru('');
    setFilterDurum('');
    setFilterKalemSube('');
    setFilterBaslangic('');
    setFilterBitis('');
    setTransferTypeFilter('tum_faturalar');
    setLogs([]);
    setTimeout(() => window.location.reload(), 80);
  };

  const handleClearTransferState = async () => {
    try {
      const keyNum = selectedInvoiceKey ? Number(selectedInvoiceKey) : NaN;
      const forOne = Number.isFinite(keyNum) && keyNum > 0;
      const res = await InvoiceService.clearTransferState(
        forOne ? { sourceInvoiceKey: keyNum } : {}
      );
      try {
        if (forOne) {
          // sadece bu fatura için kayıtları temizle
          const next = new Set<string>();
          for (const k of transferRegistryRef.current) {
            const s = String(k);
            if (!s.startsWith(`${keyNum}:`)) next.add(s);
          }
          transferRegistryRef.current = next;
          try { localStorage.setItem(TRANSFER_REGISTRY_KEY, JSON.stringify(Array.from(next))); } catch {}

          const idx = new Set<string>();
          for (const k of next) {
            const parts = String(k).split(':');
            if (parts.length < 2) continue;
            const a = Number(parts[0]);
            const b = Number(parts[1]);
            if (!Number.isFinite(a) || a <= 0 || !Number.isFinite(b) || b <= 0) continue;
            idx.add(`${a}:${b}`);
          }
          transferredLineIndexRef.current = idx;
        } else {
          transferRegistryRef.current.clear();
          transferredLineIndexRef.current.clear();
          localStorage.removeItem(TRANSFER_REGISTRY_KEY);
        }
      } catch {
        /* ignore */
      }
      try {
        if (forOne) {
          setTransferLogs(prev => {
            const next = prev.filter(x => Number(x.invoiceKey) !== keyNum);
            try { localStorage.setItem('transfer_logs_v1', JSON.stringify(next.slice(-2000))); } catch {}
            return next;
          });
        } else {
          setTransferLogs([]);
          localStorage.removeItem('transfer_logs_v1');
        }
      } catch {
        /* ignore */
      }
      pushLog({
        source_kalem_key: '',
        status: 'success',
        message: forOne
          ? `Aktarım durumu sıfırlandı (sunucu + tarayıcı kayıt). invoiceKey=${keyNum} cleared=${res.cleared}`
          : `Tüm aktarım durumu sıfırlandı (sunucu + tarayıcı kayıt). cleared=${res.cleared}`,
        was_duplicate_override: false,
      });
      if (forOne) {
        setLastTransfers(prev => {
          const next = prev.filter(t => Number(t.poolSourceInvoiceKey) !== keyNum);
          try { localStorage.setItem(LAST_TRANSFERS_KEY, JSON.stringify(next.slice(-50))); } catch {}
          return next;
        });
      } else {
        clearLastTransferState();
      }
    } catch (e: any) {
      pushLog({ source_kalem_key: '', status: 'error', message: `Aktarım durumu sıfırlanamadı: ${e?.message ?? 'hata'}`, was_duplicate_override: false });
    }
  };


  // ── Badge & yardımcılar ───────────────────────────────────────────────────

  const logCls  = (s: string) => s === 'success' ? 'erp-log-success' : s === 'duplicate' ? 'erp-log-warn' : 'erp-log-error';
  const logIcon = (s: string) => s === 'success' ? '✔' : s === 'duplicate' ? '⚠' : '✖';

  return (
    <div className="erp-root">

      {/* ════════════════════ TOPBAR ═════════════════════════════════════ */}
      <div className="erp-topbar">
        <div>
          <h1 className="erp-title">Havuz Fatura Aktarım Modülü</h1>
          <p className="erp-subtitle">
            DİA ERP &nbsp;·&nbsp; scf_fatura_liste_view ↔ scf_fatura_kalemi_liste_view
            &nbsp;·&nbsp; Kalem Bazlı Aktarım
          </p>
        </div>
        <div className="erp-topbar-actions">
          <button
            className="erp-btn erp-btn-ghost"
            onClick={handleRefresh}
            disabled={false}
          >
            {loading ? '⟳ Yükleniyor...' : '↺ Yenile'}
          </button>
          <button
            className="erp-btn erp-btn-ghost"
            onClick={handleClearTransferState}
            title="Seçili fatura varsa sadece onu; yoksa tüm sunucu duplicate/dedup kaydını + tarayıcı aktarım kaydını temizler (DİA’da sildikten sonra tekrar denemek için)."
            style={{ marginLeft: 8 }}
          >
            ⟲ Aktarım durumunu sıfırla
          </button>
        </div>
      </div>

      {/* ════════════════════ BODY ══════════════════════════════════════ */}
      <div className="erp-body">

        {/* ── FİLTRE ŞERIDI ──────────────────────────────────────────── */}
        <div className="erp-filter-bar">

          {/* Havuz Firma (sabit) */}
          <div className="erp-filter-group">
            <label>Havuz Firma</label>
            <div className="erp-muted" style={{ padding: '6px 8px', border: '1px solid #e5e7eb', borderRadius: 8, background: '#f9fafb' }}>
              Havuz Firma: {poolFirmaAdi || '—'}
            </div>
          </div>

          {/* Kaynak Dönem: kaldırıldı (dönem tarih aralığından otomatik seçilir) */}

          {/* Başlangıç / Bitiş (Rapor) */}
          <div className="erp-filter-group">
            <label>Başlangıç</label>
            <input
              type="date"
              value={filterBaslangic}
              onChange={e => setFilterBaslangic(e.target.value)}
              style={{ width: 140 }}
            />
          </div>
          <div className="erp-filter-group">
            <label>Bitiş</label>
            <input
              type="date"
              value={filterBitis}
              onChange={e => setFilterBitis(e.target.value)}
              style={{ width: 140 }}
            />
          </div>

          {/* Kaynak Şube */}
          <div className="erp-filter-group">
            <label>Kaynak Şube</label>
            <select
              value={sourceSubeKey}
              onChange={e => {
                const v = Number(e.target.value || 0);
                setSourceSubeKey(Number.isFinite(v) ? v : 0);
                if (!v) {
                  setSourceDepots([]);
                  setSourceDepoKey(0);
                }
              }}
              disabled={false}
            >
              <option value={0}>Tüm Şubeler</option>
                {branches.length === 0 && (
                  <option value={0} disabled>Şubeler yükleniyor...</option>
                )}
              {branches.map(b => (
                <option key={b.key} value={b.key}>
                  {b.subeadi}
                </option>
              ))}
            </select>
          </div>

          <div className="erp-filter-group">
            <label>Kaynak Depo</label>
            <select
              value={sourceDepoKey}
              onChange={e => {
                const v = Number(e.target.value || 0);
                setSourceDepoKey(Number.isFinite(v) ? v : 0);
              }}
              disabled={sourceSubeKey <= 0 || (sourceDepots.length === 0)}
            >
              <option value={0}>Tüm Depolar</option>
                {sourceSubeKey > 0 && sourceDepots.length === 0 && (
                  <option value={0} disabled>Depolar yükleniyor...</option>
                )}
              {sourceDepots.map(d => (
                <option key={d.key} value={d.key}>
                  {d.depoadi}
                </option>
              ))}
            </select>
          </div>

          {/* Fatura No — belgeno2 -> belgeno */}
          <div className="erp-filter-group">
            <label>Fatura No</label>
            <input
              type="text" placeholder="Örn: ABC-123"
              value={filterFaturaNo} onChange={e => setFilterFaturaNo(e.target.value)}
              style={{ width: 130 }}
            />
          </div>

          {/* Cari ünvan — cariunvan */}
          <div className="erp-filter-group">
            <label>Cari Ünvan</label>
            <input
              type="text" placeholder="Cari ara..."
              value={filterCari} onChange={e => setFilterCari(e.target.value)}
              style={{ width: 160 }}
            />
          </div>

          {/* Fatura Türü (turuack/turu_kisa) */}
          <div className="erp-filter-group">
            <label>Fatura Türü</label>
            <select
              value={filterFaturaTuru}
              onChange={e => setFilterFaturaTuru(e.target.value)}
              disabled={invoiceTypesLoading}
              style={{ width: 180 }}
            >
              <option value="">Tümü</option>
              {invoiceTypeOptions.map(t => (
                <option key={t.value} value={t.value}>{t.label}</option>
              ))}
            </select>
          </div>

          {/* Kalem Şube — client-side (RPR: __dinamik__fatsube / fatsube). Backend'e gitmez. */}
          <div className="erp-filter-group">
            <label>Kalem Şube</label>
            <input
              type="text"
              className="erp-filter-kalem-sube"
              placeholder="Örn: KONYA"
              value={filterKalemSube}
              onChange={e => setFilterKalemSube(e.target.value)}
              style={{ width: 150 }}
            />
          </div>

          {/* Durum — transfer_status [UYGULAMA ÖZEL] */}
          <div className="erp-filter-group">
            <label>Aktarım Durumu</label>
            <select value={filterDurum} onChange={e => setFilterDurum(e.target.value as '' | '0' | '1' | '2')}>
              <option value="">Tümü</option>
              <option value="0">Bekleyenler</option>
              <option value="1">Kısmi</option>
              <option value="2">Aktarıldı</option>
            </select>
          </div>

          {/* Görüntülenecek firma/dönem paneli kaldırıldı */}

          <div className="erp-filter-actions">
            <div className="erp-filter-group erp-filter-group-compact">
              <label>Liste</label>
              <div style={{ display: 'flex', gap: 6 }}>
                <button
                  type="button"
                  className={`erp-btn ${transferTypeFilter === 'tum_faturalar' ? 'erp-btn-primary' : 'erp-btn-secondary'}`}
                  onClick={() => setTransferTypeFilter('tum_faturalar')}
                  disabled={loading}
                >
                  Tüm Faturalar
                </button>
                <button
                  type="button"
                  className={`erp-btn ${transferTypeFilter === 'dagitilacak_faturalar' ? 'erp-btn-primary' : 'erp-btn-secondary'}`}
                  onClick={() => setTransferTypeFilter('dagitilacak_faturalar')}
                  disabled={loading}
                >
                  Dağıtılacak (Kalem)
                </button>
              </div>
            </div>
            <div className="erp-filter-group erp-filter-group-compact">
              <label>Üst İşlem Türü</label>
              <select
                value={ustIslemTuruFilter}
                onChange={e => setUstIslemTuruFilter(e.target.value as any)}
                style={{ width: 140 }}
              >
                <option value="">Tümü</option>
                <option value="A">A</option>
                <option value="B">B</option>
              </select>
            </div>
            <button
              className="erp-btn erp-btn-primary"
              onClick={() => fetchInvoices(true)}
              disabled={loading}
            >
              {loading ? "Yükleniyor..." : "Verileri Çek"}
            </button>
            <button className="erp-btn erp-btn-secondary" onClick={resetFilters}>
              Temizle
            </button>
          </div>
        </div>
        <div className="erp-muted" style={{ fontSize: 11, marginTop: 2, color: '#334155' }}>
          <strong>Tüm Faturalar</strong> fatura bazlı (tek satır) listeler; satıra tıklayınca kalemleri görürsün. <strong>Dağıtılacak Faturalar</strong> kalem bazlı listeler ve <strong>Kalem Şube</strong> kolonunu (dinamik) gösterir.
        </div>
        {lookupError && (
          <div className="erp-warn-box" style={{ marginTop: 6 }}>
            ⚠ {lookupError}
          </div>
        )}

        <div className="erp-filter-bar" style={{ marginTop: 8, paddingTop: 8, borderTop: '1px solid #e5e7eb' }}>
          <div className="erp-filter-actions" style={{ width: '100%', justifyContent: 'flex-start' }}>
            <button className={`erp-btn ${activeTab === 'pool' ? 'erp-btn-primary' : 'erp-btn-secondary'}`} onClick={() => setActiveTab('pool')}>
              Havuz Kayıtları
            </button>
            <button className={`erp-btn ${activeTab === 'last' ? 'erp-btn-primary' : 'erp-btn-secondary'}`} onClick={() => setActiveTab('last')}>
              Son Aktarılan Kayıt
            </button>
          </div>
        </div>

        {/* ── ANA 2 SÜTUN ─────────────────────────────────────────────── */}
        <div className="erp-main">

          {/* ════════╧ SOL SÜTUN ════════╧ */}
          <div className="erp-left-col">
            <div className="erp-tab-panel">
              {activeTab === 'pool' && (
                <>
            {/* ── 1. FATURA LİSTESİ (scf_fatura_liste_view) ────────────── */}
            <div
              className={`erp-section erp-section-invoices ${activeInvoice ? 'erp-section-invoices--split' : 'erp-section-invoices--full'}`}
            >
              <div className="erp-section-header">
                <span className="erp-section-title">
                  📋 Havuz Fatura Listesi
                  <span className="erp-src-tag">rpr_raporsonuc_getir</span>
                </span>
                <span className="erp-section-count">
                  {displayedRows.length} / {filteredRows.length} kayıt
                </span>
              </div>
              <div className="erp-table-wrap">
                <table className="erp-table">
                  <thead>
                    <tr>
                      <th style={{ width: 32 }} className="text-center">
                        <input
                          type="checkbox"
                          className="erp-cb"
                          title="Filtrelenmiş listedeki tüm faturaları seç / kaldır (sayfa dışındakiler dahil)"
                          checked={
                            isDistributableMode
                              ? (displayedRows.length > 0 && displayedRows.every(r => selectedLineKeys.has(String(r.lineKey))))
                              : (poolPageSelectableInvoiceKeys.size > 0 &&
                                [...poolPageSelectableInvoiceKeys].every(ik => selectedInvoiceKeys.has(ik)))
                          }
                          onChange={isDistributableMode ? toggleAllVisibleLines : toggleAllVisibleInvoices}
                        />
                      </th>
                      <th style={{ width: 32 }}></th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('invoiceNo')} title="Fatura No: artan/azalan sırala">
                        FATURA NO{sortIcon('invoiceNo')}
                      </th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('fisNo')} title="Fiş No: artan/azalan sırala">
                        FİŞ NO{sortIcon('fisNo')}
                      </th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('date')} title="Tarih: artan/azalan sırala">
                        TARİH{sortIcon('date')}
                      </th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('invoiceType')} title="Tür: artan/azalan sırala">
                        TÜR{sortIcon('invoiceType')}
                      </th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('upperProcess')} title="Üst İşlem: artan/azalan sırala">
                        ÜST İŞLEM{sortIcon('upperProcess')}
                      </th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('cariName')} title="Cari Ünvan: artan/azalan sırala">
                        CARİ ÜNVAN{sortIcon('cariName')}
                      </th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('sourceBranch')} title="Kaynak Şube: artan/azalan sırala">
                        KAYNAK ŞUBE{sortIcon('sourceBranch')}
                      </th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('sourceDepot')} title="Kaynak Depo: artan/azalan sırala">
                        KAYNAK DEPO{sortIcon('sourceDepot')}
                      </th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('currency')} title="Döviz: artan/azalan sırala">
                        DÖVİZ{sortIcon('currency')}
                      </th>
                      <th className="text-right" style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('total')} title="Toplam: artan/azalan sırala">
                        TOPLAM{sortIcon('total')}
                      </th>
                      <th className="text-right" style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('discount')} title="İndirim: artan/azalan sırala">
                        İNDİRİM{sortIcon('discount')}
                      </th>
                      <th className="text-right" style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('expense')} title="Masraf: artan/azalan sırala">
                        MASRAF{sortIcon('expense')}
                      </th>
                      <th className="text-right" style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('vat')} title="KDV: artan/azalan sırala">
                        KDV{sortIcon('vat')}
                      </th>
                      <th className="text-right" style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('net')} title="Net: artan/azalan sırala">
                        NET{sortIcon('net')}
                      </th>
                      <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('transferStatus')} title="Aktarım Durumu: artan/azalan sırala">
                        AKTARIM DURUMU{sortIcon('transferStatus')}
                      </th>
                      {isDistributableMode && (
                        <th style={{ cursor: 'pointer', userSelect: 'none' }} onClick={() => toggleInvoiceSort('dynamicBranch')} title="Kalem Şube: artan/azalan sırala">
                          KALEM ŞUBE{sortIcon('dynamicBranch')}
                        </th>
                      )}
                    </tr>
                  </thead>
                  <tbody>
                    {displayedRows.length === 0 && (
                      <tr><td colSpan={isDistributableMode ? 18 : 17} className="erp-empty">
                        {loading
                          ? (isDistributableMode
                              ? 'Dağıtılacak/Virman faturalar taranıyor...'
                              : 'Yükleniyor...')
                          : (isDistributableMode
                              ? 'Seçili aktarım türüne göre kayıt bulunamadı.'
                              : 'Kayıt bulunamadı.')}
                      </td></tr>
                    )}
                    {displayedRows.slice(0, 500).map((row: NormalizedRprRow, idx: number) => {
                      const invKey = Number(row.invoiceKey);
                      const invKeyOk = Number.isFinite(invKey) && invKey > 0;
                      const isActive = selectedInvoiceKey != null && Number(selectedInvoiceKey) === invKey;
                      const lineKeyStr = String(row.lineKey ?? '').trim();
                      const isInvSelected = isDistributableMode
                        ? (lineKeyStr ? selectedLineKeys.has(lineKeyStr) : false)
                        : (invKeyOk && selectedInvoiceKeys.has(invKey));
                      const isInvalid = Boolean(row.invalid);
                      const snapBad = !row.snapshotReady && !isInvalid;
                      const selectableInvoiceCb = invKeyOk;
                      const invSummary: IInvoiceListRow = {
                        key: String(invKey || ''),
                        fisno: String(row.fisNo ?? ''),
                        belgeno2: String(row.invoiceNo ?? ''),
                        tarih: String(row.date ?? ''),
                        turuack: String(row.invoiceTypeLabel ?? ''),
                        cariunvan: String(row.cariName ?? ''),
                        sourcesubeadi: String(row.sourceBranchName ?? ''),
                        sourcedepoadi: String(row.sourceWarehouseName ?? ''),
                        dovizturu: String(row.currencyCode ?? ''),
                        toplam: Number(row.invoiceTotal ?? 0),
                        toplamkdv: Number(row.invoiceVat ?? 0),
                        net: Number(row.invoiceNet ?? 0),
                        iptal: false,
                      };
                      const kalemSubeVal = String(row.dynamicBranch ?? '').replace(/[\u200B-\u200D\uFEFF]/g, '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
                      const kaynakSubeVal =
                        String(allBranchNameByKey?.[Number(row.sourceBranchKey)] ?? '').trim() ||
                        String(row.sourceBranchName ?? '').trim() ||
                        (Number(row.sourceBranchKey) > 0 ? 'Bilinmiyor' : '');
                      const depoVal =
                        String(allDepotNameByKey?.[Number(row.sourceWarehouseKey)] ?? '').trim() ||
                        String(row.sourceWarehouseName ?? '').trim() ||
                        (Number(row.sourceWarehouseKey) > 0 ? 'Bilinmiyor' : '');
                      const dovizVal = String(row.currencyCode ?? '');
                      const toplamVal = Number(row.invoiceTotal ?? 0);
                      const indirimVal = Number(row.invoiceDiscountTotal ?? 0);
                      const masrafVal = Number(row.invoiceExpenseTotal ?? 0);
                      const kdvDisplay = fmtShort(Number(row.invoiceVat ?? 0));
                      const netVal = Number(row.invoiceNet ?? 0);
                      const aktarimVal = String(row.transferStatus ?? '');
                      const aktarildi = aktarimVal.toLowerCase().includes('aktarıldı');
                      const ustIslemVal = String(row.upperProcessName || row.upperProcessCode || '').trim();

                      return (
                        <tr
                          key={String(invKey > 0 ? `${invKey}-${row.lineKey || idx}` : idx)}
                          className={[
                            'erp-row',
                            isInvalid ? 'erp-row-invalid' : '',
                            snapBad ? 'erp-row-snapshot-warn' : '',
                            isInvSelected ? 'erp-row-selected' : '',
                            isActive ? 'erp-row-active' : '',
                            aktarildi ? 'erp-row-transferred' : '',
                          ]
                            .filter(Boolean)
                            .join(' ')}
                          onClick={() => (invKey > 0 ? openInvoice(invSummary) : null)}
                        >
                          <td className="text-center">
                            <input
                              type="checkbox"
                              className="erp-cb"
                              checked={isInvSelected}
                              disabled={!selectableInvoiceCb}
                              onChange={() => {
                                if (isDistributableMode) {
                                  if (lineKeyStr) toggleLineKey(lineKeyStr);
                                } else {
                                  if (invKeyOk) toggleInvoiceKey(invKey);
                                }
                              }}
                              onClick={e => e.stopPropagation()}
                              title="Aktarım için faturayı seç / kaldır"
                              aria-checked={isInvSelected}
                            />
                          </td>
                          <td className="text-center">
                            <input type="radio" readOnly checked={isActive} className="erp-radio" />
                          </td>
                          <td className="erp-fisno">{String(row.invoiceNo) || ''}</td>
                          <td className="erp-fisno">{String(row.fisNo) || ''}</td>
                          <td className="erp-date">{row.date ? new Date(String(row.date)).toLocaleDateString('tr-TR') : ''}</td>
                          <td className="erp-muted erp-cell-std">{String(row.invoiceTypeLabel) || ''}</td>
                          <td className="erp-muted erp-cell-std">{ustIslemVal || ''}</td>
                          <td className="erp-cari">{String(row.cariName) || ''}</td>
                          <td className="erp-muted">{String(kaynakSubeVal || '')}</td>
                          <td className="erp-muted">{String(depoVal || '')}</td>
                          <td className="erp-muted">{String(dovizVal || '')}</td>
                          <td className="text-right erp-amount">{fmtShort(Number(toplamVal) || 0)}</td>
                          <td className="text-right erp-amount">{fmtShort(Number(indirimVal) || 0)}</td>
                          <td className="text-right erp-amount">{fmtShort(Number(masrafVal) || 0)}</td>
                          <td className="text-right erp-amount">{String(kdvDisplay || '')}</td>
                          <td className="text-right erp-amount">{fmtShort(Number(netVal) || 0)}</td>
                          <td className="erp-cell-std">
                            <span className={transferStatusCellClass(aktarimVal)}>{String(aktarimVal || '')}</span>
                          </td>
                          {isDistributableMode && (
                            <td>
                              <span className="erp-sube-tag erp-sube-tag--accent">{String(kalemSubeVal || '')}</span>
                            </td>
                          )}
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
                </>
              )}

            {activeTab === 'pool' && activeInvoice && (
            <div className="erp-section erp-section-lines">
              <div className="erp-section-header">
                <span className="erp-section-title">
                  {activeInvoice
                    ? <>📄 Kalemler — <strong>{activeInvoice.fisno}</strong>
                        <span className="erp-cari-name"> · {activeInvoice.cariunvan}</span>
                        <span className="erp-src-tag">scf_fatura_getir.result.m_kalemler</span>
                      </>
                    : '📄 Kalem Tablosu — Listeden bir fatura seçin'
                  }
                </span>
                {activeInvoice && (
                  <span className="erp-section-count">{activeInvoiceRprLines.length} kalem</span>
                )}
              </div>

              <div className="erp-table-wrap">
                {!activeInvoice ? (
                  <div className="erp-empty" style={{ color: '#9ca3af' }}>
                    ↑ Kalemlerini görüntülemek için yukarıdan bir fatura seçin.
                  </div>
                ) : (
                  <table className="erp-table">
                    <thead>
                      <tr>
                        <th style={{ width: 30 }}></th>
                        <th style={{ width: 40 }}>SIRA</th>
                        <th>KALEM TİPİ</th>
                        <th>STOK/HİZMET KODU</th>
                        <th>AÇIKLAMA</th>
                        <th>BİRİM</th>
                        <th className="text-right">MİKTAR</th>
                        <th className="text-right">BİRİM FİYAT</th>
                        <th className="text-right">TUTAR</th>
                        <th className="text-right">İNDİRİM</th>
                        <th className="text-right">MASRAF</th>
                        <th>KALEM ŞUBE</th>
                      </tr>
                    </thead>
                    <tbody>
                      {activeInvoiceRprLines.map((r, idx) => {
                        const lk = String(r.lineKey || idx);
                        const invK = Number(r.invoiceKey);
                        const invChosen = Number.isFinite(invK) && invK > 0 && selectedInvoiceKeys.has(invK);
                        const lineKeyStr = String(r.lineKey ?? '').trim();
                        const checked = isDistributableMode
                          ? (lineKeyStr ? selectedLineKeys.has(lineKeyStr) : false)
                          : (lineKeyStr ? selectedLineKeys.has(lineKeyStr) : invChosen);
                        const aktarimVal = String(r.transferStatus ?? '');
                        const aktarildi = aktarimVal.toLowerCase().includes('aktarıldı');
                        const isInv = Boolean(r.invalid);
                        const lineSnapBad = !isInv && !r.snapshotReady;
                        const lineSelectable = Number.isFinite(invK) && invK > 0;
                        return (
                          <tr
                            key={lk}
                            className={[
                              'erp-row',
                              isInv ? 'erp-row-invalid' : '',
                              lineSnapBad ? 'erp-row-snapshot-warn' : '',
                              checked ? 'erp-row-selected' : '',
                              aktarildi ? 'erp-row-transferred' : '',
                            ]
                              .filter(Boolean)
                              .join(' ')}
                            onClick={() => {
                              if (!lineSelectable) return;
                              if (isDistributableMode) {
                                if (lineKeyStr) toggleLineKey(lineKeyStr);
                              } else {
                                if (lineKeyStr) toggleLineKeyForInvoice(invK, lineKeyStr);
                              }
                            }}
                          >
                            <td className="text-center">
                              <input
                                type="checkbox"
                                className="erp-cb"
                                checked={checked}
                                disabled={!lineSelectable}
                                onChange={() => {
                                  if (!lineSelectable) return;
                                  if (isDistributableMode) {
                                    if (lineKeyStr) toggleLineKey(lineKeyStr);
                                  } else {
                                    if (lineKeyStr) toggleLineKeyForInvoice(invK, lineKeyStr);
                                  }
                                }}
                                onClick={e => e.stopPropagation()}
                              />
                            </td>
                            <td className="text-center erp-muted erp-cell-std">{idx + 1}</td>
                            <td className="erp-muted erp-cell-std">{String(r.lineTypeLabel ?? '')}</td>
                            <td className="erp-mono erp-fisno erp-cell-std">{String(r.itemCode ?? '')}</td>
                            <td style={{ fontWeight: checked ? 600 : 400, minWidth: 160 }}>{String(r.itemName ?? '')}</td>
                            <td className="erp-muted">{String(r.unitName ?? '')}</td>
                            <td className="text-right">{Number(r.quantity ?? 0).toLocaleString('tr-TR')}</td>
                            <td className="text-right erp-amount">{fmtShort(Number(r.unitPrice ?? 0) || 0)}</td>
                            <td className="text-right erp-amount erp-bold">{fmtShort(Number(r.lineTotal ?? 0) || 0)}</td>
                            <td className="text-right erp-muted">{fmtShort(Number(r.lineDiscount ?? 0) || 0)}</td>
                            <td className="text-right erp-muted">{fmtShort(Number(r.lineExpense ?? 0) || 0)}</td>
                            <td className="erp-muted erp-cell-std">
                              <span className="erp-sube-tag erp-sube-tag--accent">{String(r.dynamicBranch ?? '')}</span>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                )}
              </div>
            </div>
            )}

            {/* ── 3. İŞLEM GÜNLÜĞÜ (minimal / kapalı) ───────────────────── */}
            {activeTab === 'pool' && logs.length > 0 && (
              <div className="erp-section erp-section-log">
                <div className="erp-section-header">
                  <span className="erp-section-title">📋 İşlem Günlüğü</span>
                  <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                    <button
                      className="erp-btn erp-btn-ghost"
                      style={{ fontSize: 10, padding: '1px 8px', height: 22 }}
                      onClick={() => setShowLogPanel(v => !v)}
                    >
                      {showLogPanel ? 'Gizle' : `Göster (${logs.length})`}
                    </button>
                    <button
                      className="erp-btn erp-btn-ghost"
                      style={{ fontSize: 10, padding: '1px 8px', height: 22 }}
                      onClick={() => setLogs([])}
                    >
                      Temizle
                    </button>
                  </div>
                </div>
                {showLogPanel && (
                  <div className="erp-log-panel">
                    {logs.slice(0, 25).map((l, i) => (
                      <div key={i} className={`erp-log-row ${logCls(l.status)}`}>
                        <span className="erp-log-icon">{logIcon(l.status)}</span>
                        {l.stok_hizmet_kodu && <strong>{l.stok_hizmet_kodu}</strong>}
                        {l.message}
                      </div>
                    ))}
                    {logs.length > 25 && (
                      <div className="erp-log-row erp-log-warn" style={{ opacity: 0.9 }}>
                        <span className="erp-log-icon">…</span>
                        Daha fazla kayıt gizlendi. (F12 → Console: debug_logs_v1=1 ile ayrıntı)
                      </div>
                    )}
                  </div>
                )}
              </div>
            )}

              {activeTab === 'last' && (
                <div className="erp-section erp-section-fill erp-section-viewer">
                  <div className="erp-section-header">
                    <span className="erp-section-title">🧾 Son Aktarılan Kayıt</span>
                    <span className="erp-section-count">
                      {groupedLastTransfers.length} fatura
                    </span>
                  </div>
                  <div className="erp-table-wrap">
                    <table className="erp-table">
                      <thead>
                        <tr>
                          <th
                            style={{ cursor: 'pointer', userSelect: 'none' }}
                            onClick={() => toggleInvoiceSort('invoiceNo')}
                            title="Fatura No: artan/azalan sırala"
                          >
                            FATURA NO{sortIcon('invoiceNo')}
                          </th>
                          <th
                            style={{ cursor: 'pointer', userSelect: 'none' }}
                            onClick={() => toggleInvoiceSort('fisNo')}
                            title="Fiş No: artan/azalan sırala"
                          >
                            FİŞ NO{sortIcon('fisNo')}
                          </th>
                          <th>TARİH</th>
                          <th>TÜR</th>
                          <th>ÜST İŞLEM</th>
                          <th>CARİ ÜNVAN</th>
                          <th>KAYNAK ŞUBE</th>
                          <th>KAYNAK DEPO</th>
                          <th>DÖVİZ</th>
                          <th className="text-right">TOPLAM</th>
                          <th className="text-right">İNDİRİM</th>
                          <th className="text-right">MASRAF</th>
                          <th className="text-right">KDV</th>
                          <th className="text-right">NET</th>
                          <th>AKTARIM DURUMU</th>
                          <th>DAĞITIM</th>
                        </tr>
                      </thead>
                      <tbody>
                        {groupedLastTransfers.length === 0 && (
                          <tr>
                            <td colSpan={16} className="erp-empty">
                              Filtreye uyan son aktarılan kayıt yok.
                            </td>
                          </tr>
                        )}
                        {groupedLastTransfers.map(g => {
                          const t = g.header;
                          const lastSt = String(t.poolTransferStatus || 'Aktarıldı');
                          const isOpen = expandedLastInvoiceKeys.has(g.invKey);
                          return (
                          <>
                          <tr
                            key={`last_inv_${g.invKey}`}
                            className="erp-row"
                            onClick={() => toggleExpandedLastInvoiceKey(g.invKey)}
                            style={{ cursor: g.lineCount > 0 ? 'pointer' : 'default' }}
                          >
                            <td className="erp-fisno">
                              <div>{t.poolInvoiceNo || `KEY#${t.createdInvoiceKey}`}</div>
                              {t.poolInvoiceNo ? (
                                <div className="erp-muted" style={{ fontSize: 11 }}>
                                  {t.createdInvoiceKey > 0 ? `Hedef fatura _key ${t.createdInvoiceKey}` : 'Hedef fatura _key —'}
                                  {t.poolSourceInvoiceKey != null && t.poolSourceInvoiceKey > 0
                                    ? ` · havuz ${t.poolSourceInvoiceKey}`
                                    : ''}
                                  {g.lineCount > 0 ? ` · ${g.lineCount} kalem` : ''}
                                  {g.branchSummary ? ` · ${g.branchSummary}` : ''}
                                </div>
                              ) : null}
                            </td>
                            <td className="erp-muted erp-cell-std">{t.poolFisNo ? t.poolFisNo : '—'}</td>
                            <td className="erp-date">
                              {t.poolDate
                                ? Number.isFinite(Date.parse(t.poolDate))
                                  ? new Date(t.poolDate).toLocaleDateString('tr-TR')
                                  : t.poolDate
                                : '—'}
                            </td>
                            <td className="erp-muted erp-cell-std">FATURA</td>
                            <td className="erp-muted erp-cell-std">{t.poolUpperProcess || '—'}</td>
                            <td className="erp-cari">
                              {(t.poolCariKod || t.targetCariKod) ? (
                                <div className="erp-mono" style={{ fontSize: 10, color: '#374151', marginBottom: 2 }}>
                                  {t.poolCariKod || t.targetCariKod}
                                </div>
                              ) : null}
                              <div>{t.poolCariUnvan || t.targetCariUnvan || '—'}</div>
                            </td>
                            <td className="erp-muted erp-cell-std">{t.poolKaynakSube || '—'}</td>
                            <td className="erp-muted erp-cell-std">{t.poolKaynakDepo || '—'}</td>
                            <td className="erp-muted erp-cell-std">{t.poolDoviz || '—'}</td>
                            <td className="text-right erp-amount">
                              {t.poolToplam != null && Number.isFinite(t.poolToplam) ? fmtShort(t.poolToplam) : '—'}
                            </td>
                            <td className="text-right erp-amount">
                              {t.poolIndirim != null && Number.isFinite(t.poolIndirim) ? fmtShort(t.poolIndirim) : '—'}
                            </td>
                            <td className="text-right erp-amount">
                              {t.poolMasraf != null && Number.isFinite(t.poolMasraf) ? fmtShort(t.poolMasraf) : '—'}
                            </td>
                            <td className="text-right erp-amount">
                              {t.poolKdv != null && Number.isFinite(t.poolKdv) ? fmtShort(t.poolKdv) : '—'}
                            </td>
                            <td className="text-right erp-amount erp-bold">
                              {t.poolNet != null && Number.isFinite(t.poolNet) ? fmtShort(t.poolNet) : '—'}
                            </td>
                            <td className="erp-cell-std">
                              <span className={transferStatusCellClass(lastSt)}>{lastSt}</span>
                            </td>
                            <td className="erp-cell-std">
                              {g.lineCount > 0 ? (
                                <span className="erp-sube-tag erp-sube-tag--accent">
                                  {g.branchSummary ? g.branchSummary : `${g.lineCount} kalem`}
                                </span>
                              ) : (
                                <span className="erp-muted">—</span>
                              )}
                            </td>
                          </tr>
                          {isOpen && g.lines.length > 0 && (
                            <tr key={`last_inv_${g.invKey}_detail`}>
                              <td colSpan={16} style={{ padding: '8px 10px', background: '#f8fafc' }}>
                                <div style={{ display: 'flex', justifyContent: 'space-between', gap: 10, marginBottom: 6 }}>
                                  <div className="erp-muted" style={{ fontSize: 12 }}>
                                    Aktarılan kalemler
                                    {g.branchSummary ? ` · Şubeler: ${g.branchSummary}` : ''}
                                  </div>
                                </div>
                                <div className="erp-table-wrap" style={{ maxHeight: 260, border: '1px solid #d7dde7', borderRadius: 6 }}>
                                  <table className="erp-table erp-table-nested" style={{ margin: 0 }}>
                                  <thead>
                                    <tr>
                                      <th style={{ width: 30 }}></th>
                                      <th style={{ width: 40 }}>SIRA</th>
                                      <th>KALEM TİPİ</th>
                                      <th>STOK/HİZMET KODU</th>
                                      <th>AÇIKLAMA</th>
                                      <th>BİRİM</th>
                                      <th className="text-right">MİKTAR</th>
                                      <th className="text-right">BİRİM FİYAT</th>
                                      <th className="text-right">TUTAR</th>
                                      <th className="text-right">İNDİRİM</th>
                                      <th className="text-right">MASRAF</th>
                                      <th>KALEM ŞUBE</th>
                                    </tr>
                                  </thead>
                                  <tbody>
                                    {/* Header bazı tarayıcı/scroll kombinasyonlarında görünmeyebiliyor.
                                        Bu yüzden "görsel başlık satırı"nı da tablo içine sabit ekliyoruz. */}
                                    <tr className="erp-fake-thead">
                                      <td className="text-center"></td>
                                      <td className="text-center">SIRA</td>
                                      <td>KALEM TİPİ</td>
                                      <td>STOK/HİZMET KODU</td>
                                      <td>AÇIKLAMA</td>
                                      <td>BİRİM</td>
                                      <td className="text-right">MİKTAR</td>
                                      <td className="text-right">BİRİM FİYAT</td>
                                      <td className="text-right">TUTAR</td>
                                      <td className="text-right">İNDİRİM</td>
                                      <td className="text-right">MASRAF</td>
                                      <td>KALEM ŞUBE</td>
                                    </tr>
                                    {g.lines.map((ln: any, i: number) => {
                                      const lk = Number(ln.poolSourceLineKey);
                                      const r =
                                        allRows.find(r0 => Number(r0.invoiceKey) === g.invKey && Number(r0.lineKey) === lk && !r0.invalid)
                                        ?? allRows.find(r0 => Number(r0.invoiceKey) === g.invKey && Number(r0.lineKey) === lk);

                                      const lineTypeLabel = String(r?.lineTypeLabel ?? ln.lineTypeLabel ?? '').trim();
                                      const itemCode = String(r?.itemCode ?? ln.lineItemCode ?? '').trim();
                                      const itemName = String(r?.itemName ?? ln.lineItemName ?? '').trim();
                                      const unitName = String(r?.unitName ?? ln.lineUnitName ?? '').trim();
                                      const qty = Number(r?.quantity ?? ln.lineQuantity ?? 0);
                                      const unitPrice = Number(r?.unitPrice ?? 0);
                                      const lineTotal = Number(r?.lineTotal ?? ln.lineTotal ?? 0);
                                      const disc = Number(r?.lineDiscount ?? 0);
                                      const exp = Number(r?.lineExpense ?? 0);
                                      const dyn = String(r?.dynamicBranch ?? ln.poolKalemSube ?? '').replace(/[\u200B-\u200D\uFEFF]/g, '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();

                                      return (
                                        <tr key={`last_line_${g.invKey}_${lk}_${i}`} className="erp-row">
                                          <td className="text-center">
                                            <input type="checkbox" className="erp-cb" checked readOnly disabled />
                                          </td>
                                          <td className="text-center erp-muted erp-cell-std">{i + 1}</td>
                                          <td className="erp-muted erp-cell-std">{lineTypeLabel || '—'}</td>
                                          <td className="erp-mono erp-fisno erp-cell-std">{itemCode || '—'}</td>
                                          <td style={{ minWidth: 160 }}>{itemName || '—'}</td>
                                          <td className="erp-muted">{unitName || '—'}</td>
                                          <td className="text-right">{Number(qty ?? 0).toLocaleString('tr-TR')}</td>
                                          <td className="text-right erp-amount">{Number.isFinite(unitPrice) ? fmtShort(unitPrice) : '—'}</td>
                                          <td className="text-right erp-amount erp-bold">{Number.isFinite(lineTotal) ? fmtShort(lineTotal) : '—'}</td>
                                          <td className="text-right erp-muted">{Number.isFinite(disc) ? fmtShort(disc) : '—'}</td>
                                          <td className="text-right erp-muted">{Number.isFinite(exp) ? fmtShort(exp) : '—'}</td>
                                          <td className="erp-muted erp-cell-std">
                                            <span className="erp-sube-tag erp-sube-tag--accent">{dyn || '—'}</span>
                                          </td>
                                        </tr>
                                      );
                                    })}
                                  </tbody>
                                  </table>
                                </div>
                              </td>
                            </tr>
                          )}
                          </>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}
            </div>
          </div>{/* /.erp-left-col */}

          {/* ════════╧ SAĞ PANEL (Aktarım) ════════╧ */}
          {activeTab === 'pool' && (
            <div className="erp-right-col">
              <div className="erp-action-panel">

              <div className="erp-action-title">HEDEF AKTARIM — ★ UYGULAMA ÖZEL</div>

              {transferFlags.transferRawMode && (
                <div className="erp-warn-box" style={{ marginBottom: 10 }}>
                  TransferRawMode açık: başlıkta cari ve döviz kodları hedef firmada otomatik _key’e çözülür.
                  Satır tarafı (kalem türü, birim, tutar vb.) snapshot ile uyumlu olmalıdır.
                </div>
              )}

              {/* Hedef Firma — sis_kullanici_firma_parametreleri */}
              <div className="erp-form-row">
                <label>Normal Firmalar / Hedef Firma <span className="erp-src-label">sis_firma</span></label>
                <select
                  value={targetFirmaKodu}
                  onChange={e => handleTargetFirmaChange(e.target.value)}
                  onFocus={() => { ensureCompaniesLoaded(true); }}
                >
                  <option value="">— Seçiniz —</option>
                  {companies.filter(f => f.firma_kodu !== poolFirmaKodu).map(f => (
                    <option key={f.firma_kodu} value={f.firma_kodu}>
                      {f.firma_kodu} — {f.firma_adi || `Firma ${f.firma_kodu}`}
                    </option>
                  ))}
                </select>
                {companies.filter(f => f.firma_kodu !== poolFirmaKodu).length === 0 && (
                  <div className="erp-muted" style={{ fontSize: 11 }}>
                    Hedef firma listesi boş görünüyor. Lookuplar yeniden yükleniyor; gerekirse sayfayı yenileyin.
                  </div>
                )}
              </div>


              {/* Hedef Şube — sis_sube (key sis_firma filtreli) */}
              <div className="erp-form-row">
                <label>Hedef Şube <span className="erp-src-label">sis_sube</span></label>
                <select
                  value={targetSubeKey}
                  onChange={(e) => {
                    const raw = e.target.value;
                    const v = raw ? Number(raw) : '';
                    setTargetSubeKey(v);
                    const hit = allowedTargetBranches.find(x => String(x.key) === String(raw));
                    setTargetSubeAdi(hit?.subeadi ?? '');
                    // Şube değişince depo seçimi sıfırlansın; depo listesi effect ile yüklenecek.
                    setTargetDepoKey('');
                  }}
                  disabled={!targetFirmaKodu || allowedTargetBranches.length === 0}

                >
                  <option value="">— Seçiniz —</option>
                  {allowedTargetBranches.map(s => (
                    <option key={s.key} value={s.key}>
                      {s.subeadi}
                    </option>
                  ))}
                </select>
                <div className="erp-muted" style={{ fontSize: 11 }}>
                  Hedef şube otomatik seçilir; gerekirse buradan değiştirebilirsiniz.
                </div>
                {targetFirmaKodu && allowedTargetBranches.length === 0 && (
                  <div className="erp-muted" style={{ fontSize: 11 }}>
                    Bu firmada yetkili şube bulunamadı.
                  </div>
                )}
              </div>

              <div className="erp-form-row">
                <label>Hedef Depo <span className="erp-src-label">sis_depo</span></label>
                <select
                  value={targetDepoKey}
                  onChange={(e) => {
                    const raw = e.target.value;
                    setTargetDepoKey(raw ? Number(raw) : '');
                  }}
                  disabled={!targetSubeKey || allowedTargetDepots.length === 0}
                >
                  <option value="">— Depo —</option>
                  {allowedTargetDepots.map(d => (
                    <option key={d.key} value={d.key}>
                      {d.depoadi}
                    </option>
                  ))}
                </select>
                <div className="erp-muted" style={{ fontSize: 11 }}>
                  Hedef depo otomatik seçilir; gerekirse buradan değiştirebilirsiniz.
                </div>
                {targetSubeKey && allowedTargetDepots.length === 0 && (
                  <div className="erp-muted" style={{ fontSize: 11 }}>
                    Bu şube için depo bulunamadı.
                  </div>
                )}
              </div>

              {/* Hedef Dönem — sis_donem (_key_sis_firma filtreli) */}
              <div className="erp-form-row">
                <label>Dönem <span className="erp-src-label">sis_donem</span></label>
                <select
                  value={targetDonemKey === '' ? '' : String(targetDonemKey)}
                  onChange={(e) => {
                    const raw = e.target.value;
                    const v = raw ? Number(raw) : '';
                    setTargetDonemKey(v);
                    const hit = targetPeriods.find(p => String(p.key) === String(raw));
                    setTargetDonemLabel(hit?.gorunenkod ?? '');
                    setTargetDonemKodu(hit?.donemkodu ?? '');
                  }}
                  disabled={!targetFirmaKodu || targetPeriods.length === 0}
                >
                  <option value="">— Dönem —</option>
                  {targetPeriods.map(d => (
                    <option key={d.key} value={String(d.key)}>
                      {d.gorunenkod}{d.ontanimli ? ' (Önt.)' : ''}
                    </option>
                  ))}
                </select>
                {targetFirmaKodu && targetPeriods.length === 0 && (
                  <div className="erp-muted" style={{ fontSize: 11 }}>
                    Bu firmada yetkili dönem bulunamadı.
                  </div>
                )}
                <div className="erp-muted" style={{ fontSize: 11 }}>
                  Dönem kaynak fatura tarihine göre otomatik seçilir; gerekirse buradan değiştirebilirsiniz.
                </div>
                {targetResolveInfo && (
                  <div className="erp-muted" style={{ fontSize: 11, color: '#1d4ed8' }}>
                    {targetResolveInfo}
                  </div>
                )}
              </div>


              <div className="erp-divider" />

              {/* Seçim özeti */}
              <div className="erp-summary">
                <div className="erp-summary-row">
                  <span>Seçilen Fatura</span>
                  <strong className={selectedInvoiceKeys.size > 0 ? 'erp-summary-highlight' : ''}>
                    {selectedInvoiceKeys.size > 0 ? `${selectedInvoiceKeys.size} adet` : '—'}
                  </strong>
                </div>
              <div className="erp-summary-row">
                <span>Invoice Key</span>
                <span style={{ fontSize: 11, color: '#374151' }}>
                  {selectedInvoiceKey ?? '—'}
                </span>
              </div>
                <div className="erp-summary-row">
                  <span>Aktarım Türü</span>
                  <strong> {calculatedTransferType}</strong>
                </div>
                <div className="erp-summary-row">
                  <span>Hedef Firma</span>
                  <strong>{targetFirmaAdi || '—'}</strong>
                </div>
                <div className="erp-summary-row">
                  <span>Hedef Şube</span>
                  <strong>{targetSubeAdi || '—'}</strong>
                </div>
                <div className="erp-summary-row">
                  <span>Hedef Depo</span>
                  <strong>{(targetDepots.find(d => d.key === Number(targetDepoKey))?.depoadi) || '—'}</strong>
                </div>
                <div className="erp-summary-row">
                  <span>Hedef Dönem</span>
                  <strong>{targetDonemLabel || (targetDonemKodu || '—')}</strong>
                </div>
                <div style={{ fontSize: 11, color: '#374151', marginTop: -2, marginBottom: 4 }}>
                  {transferDecisionReason}
                </div>
                {isDistributableMode && distributionSummary.length > 1 && (
                  <div style={{ fontSize: 11, color: '#374151', marginTop: 4 }}>
                    <div style={{ fontWeight: 700, marginBottom: 2 }}>Şube Dağılımı</div>
                    {distributionSummary.map(x => (
                      <div key={x.branch}>
                        {x.branch}: <strong>{x.count}</strong> kalem
                      </div>
                    ))}
                    <div className="erp-muted" style={{ marginTop: 4 }}>
                      Birden fazla şube var; “tek şube/depo kilidi” uygulanmaz.
                    </div>
                  </div>
                )}
                <div className="erp-summary-row">
                  <span>Cari Ünvan</span>
                  <span style={{ fontSize: 11, color: '#374151' }}>
                    {activeInvoice ? activeInvoice.cariunvan : '—'}
                  </span>
                </div>
                <div className="erp-summary-row">
                  <span>Seçilen Kalem</span>
                  <strong className={selectedPoolRows.length > 0 ? 'erp-summary-highlight' : ''}>
                    {selectedPoolRows.length > 0
                      ? `${selectedPoolRows.length} adet (havuz)`
                      : selectedKalemKeys.length > 0
                        ? `${selectedKalemKeys.length} adet (detay)`
                        : '—'}
                  </strong>
                </div>
                {selectedInvoiceKeys.size === 0 && selectedKalemKeys.length === 0 ? (
                  <div style={{ fontSize: 11, color: '#0f766e' }}>
                    Havuz listesinde fatura satırındaki onay kutusuyla seçim yapın; kalemler seçilen faturaya göre otomatik eşlenir.
                  </div>
                ) : selectedInvoiceKeys.size > 0 ? (
                  <div style={{ fontSize: 11, color: '#374151' }}>
                    Aktarım fatura bazlıdır; doğrulama sunucuda yapılır.
                  </div>
                ) : (
                  <div style={{ fontSize: 11, color: '#374151' }}>
                    Seçilen key: {selectedKalemKeys.slice(0, 5).join(', ')}{selectedKalemKeys.length > 5 ? ' ...' : ''}
                  </div>
                )}
                <div className="erp-summary-row">
                  <span>Aktarım Tutarı</span>
                  <strong className="erp-summary-amount">{fmt(selectedTotal)}</strong>
                </div>
                <div className="erp-summary-row">
                  <span>KDV Tutarı</span>
                  <strong style={{ color: '#374151' }}>{fmt(selectedKdvTotal)}</strong>
                </div>
              </div>

              {/* MÜKERRER UYARISI */}
              {duplicateRiskCount > 0 && (
                <div className="erp-warn-box">
                  ⚠ {duplicateRiskCount} kalem bu hedefe zaten aktarılmış!
                  Backend mükerrer olarak atlayacak ve loglayacak.
                </div>
              )}

              {targetSelectionRuleError && (
                <div className="erp-error-box">
                  ⛔ {targetSelectionRuleError}
                </div>
              )}
              {transferAlert && (
                <div className="erp-error-box">
                  ⛔ {transferAlert}
                </div>
              )}
              {bulkTransferSummary && (
                <div className="erp-hint-box" style={{ marginTop: 8 }}>
                  <div><strong>Aktarım Özeti</strong>: başarılı={bulkTransferSummary.ok} hatalı={bulkTransferSummary.error}</div>
                  {bulkTransferSummary.messages.length > 0 && (
                    <div style={{ marginTop: 6, fontSize: 12 }}>
                      {bulkTransferSummary.messages.map((m, i) => (
                        <div key={i}>- {m}</div>
                      ))}
                    </div>
                  )}
                </div>
              )}

              {transferProgress && (
                <div className="erp-hint-box" style={{ marginTop: 8 }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap' }}>
                    <div><strong>Progress</strong>: toplam={transferProgress.total} başarı={transferProgress.success} hata={transferProgress.failed} kalan={transferProgress.remaining} işlemde={transferProgress.inFlight}</div>
                  </div>
                  <div style={{ height: 8, background: '#e5e7eb', borderRadius: 6, overflow: 'hidden', marginTop: 6 }}>
                    <div
                      style={{
                        height: 8,
                        width: `${transferProgress.total > 0 ? Math.round(((transferProgress.success + transferProgress.failed) / transferProgress.total) * 100) : 0}%`,
                        background: '#2563eb',
                      }}
                    />
                  </div>
                </div>
              )}

              {transferLogs.length > 0 && (
                <div className="erp-section" style={{ marginTop: 10 }}>
                  <div className="erp-section-header">
                    <span className="erp-section-title">📜 Aktarım Logları</span>
                    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                      <button
                        className="erp-btn erp-btn-ghost"
                        style={{ fontSize: 10, padding: '1px 8px', height: 22 }}
                        onClick={() => setShowTransferLogPanel(v => !v)}
                      >
                        {showTransferLogPanel ? 'Gizle' : `Göster (${transferLogs.length})`}
                      </button>
                      <button
                        className="erp-btn erp-btn-ghost"
                        style={{ fontSize: 10, padding: '1px 8px', height: 22 }}
                        onClick={() => {
                          setTransferLogs([]);
                          try { localStorage.removeItem('transfer_logs_v1'); } catch {}
                        }}
                      >
                        Temizle
                      </button>
                    </div>
                  </div>
                  {showTransferLogPanel && (
                    <div className="erp-log-panel">
                      {transferLogs.slice(-50).reverse().map((l, i) => (
                        <div key={i} className={`erp-log-row ${l.success ? 'log-success' : 'log-error'}`}>
                          <span className="erp-log-icon">{l.success ? '✓' : '✕'}</span>
                          <strong>{String(l.invoiceKey)}</strong>
                          <span style={{ marginLeft: 8 }}>kalem={l.lineCount}</span>
                          <span style={{ marginLeft: 8 }}>firma={l.targetFirma}</span>
                          <span style={{ marginLeft: 8, color: '#6b7280' }}>{new Date(l.timestamp).toLocaleString('tr-TR')}</span>
                          {!l.success && l.errorMessage && (
                            <span style={{ marginLeft: 10, color: '#991b1b' }}>{l.errorMessage}</span>
                          )}
                        </div>
                      ))}
                      {transferLogs.length > 50 && (
                        <div className="erp-log-row erp-log-warn" style={{ opacity: 0.9 }}>
                          <span className="erp-log-icon">…</span>
                          Daha fazlası gizlendi.
                        </div>
                      )}
                    </div>
                  )}
                </div>
              )}

              {/* Disabled açıklaması */}
              {!canTransfer && (
                <div className="erp-hint-box">
                  {transferBlockers.map(reason => (
                    <div key={reason}>• {reason}</div>
                  ))}
                </div>

              )}

              {/* ANA AKSİYON BUTONU */}
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
                <button
                  className={`erp-btn erp-btn-transfer ${canTransfer ? 'erp-btn-transfer-active' : ''}`}
                  onClick={handleTransfer}
                  disabled={!canTransfer || transferring}
                  title={canTransfer ? 'Aktarımı başlat' : 'Kalem, firma, şube ve dönem seçin'}
                >
                  {transferring
                    ? '⟳ Aktarılıyor...'
                    : `➤ ${selectedPoolRows.length} KALEMİ AKTAR`}
                </button>

                {transferring && (
                  <button className="erp-btn erp-btn-secondary" onClick={cancelTransfer}>
                    İptal Et
                  </button>
                )}

                {!transferring && failedInvoices.length > 0 && (
                  <button className="erp-btn erp-btn-secondary" onClick={retryFailed}>
                    Tekrar Dene ({failedInvoices.length})
                  </button>
                )}
              </div>

              {failedInvoices.length > 0 && !transferring && (
                <div className="erp-warn-box" style={{ marginTop: 8 }}>
                  ⚠ {failedInvoices.length} fatura başarısız oldu. “Tekrar Dene” ile sadece bunlar tekrar gönderilir.
                </div>
              )}

              {lastTransfer && (
                <div className="erp-hint-box" style={{ marginTop: 10 }}>
                  <div><strong>Son Aktarım</strong></div>
                  <div>createdInvoiceKey: <strong>{lastTransfer.createdInvoiceKey}</strong></div>
                  <div style={{ fontSize: 11, color: '#374151' }}>
                    Hedef: firma={lastTransfer.targetFirmaKodu} ({lastTransfer.targetFirmaAdi || '—'}) dönem={lastTransfer.targetDonemKodu} şubeKey={lastTransfer.targetSubeKey} depoKey={lastTransfer.targetDepoKey}
                  </div>
                  {lastTransfer.targetFisNo && (
                    <div>Hedef fiş no: <strong>{lastTransfer.targetFisNo}</strong></div>
                  )}
                  {lastTransfer.targetCariKod && (
                    <div>Hedef cari kod: <strong>{lastTransfer.targetCariKod}</strong></div>
                  )}
                  {lastTransfer.targetCariUnvan && (
                    <div>Hedef cari ünvan: <strong>{lastTransfer.targetCariUnvan}</strong></div>
                  )}
                  <div style={{ display: 'flex', gap: 8, marginTop: 8, flexWrap: 'wrap' }}>
                    <button
                      className="erp-btn erp-btn-secondary"
                      onClick={async () => {
                        try {
                          const r = await InvoiceService.getInvoice(lastTransfer.createdInvoiceKey, {
                            firmaKodu: lastTransfer.targetFirmaKodu,
                            donemKodu: lastTransfer.targetDonemKodu,
                          });
                          const fisno = (r.data as any)?.fisno ?? (r.data as any)?.FisNo;
                          const cariKod = (r.data as any)?.carikartkodu ?? (r.data as any)?.CariKartKodu;
                          const cariUnvan = (r.data as any)?.cariunvan ?? (r.data as any)?.CariUnvan;
                          setLastTransfers(prev => prev.map(x => x.createdInvoiceKey === lastTransfer.createdInvoiceKey ? ({ ...x, targetFisNo: fisno, targetCariKod: cariKod, targetCariUnvan: cariUnvan }) : x));
                          // Doğrulama başarısı kullanıcı günlüğünü şişirmesin.
                          if (debugEnabled) {
                            // eslint-disable-next-line no-console
                            console.debug('[verify] ok', { key: lastTransfer.createdInvoiceKey, fisno: fisno ?? '—' });
                          }
                        } catch (e: any) {
                          pushLog({ source_kalem_key: '', status: 'error', message: `Hedef fatura doğrulanamadı. key=${lastTransfer.createdInvoiceKey} (${e?.message ?? 'hata'})`, was_duplicate_override: false });
                        }
                      }}
                    >
                      Hedef faturayı doğrula
                    </button>
                  </div>
                </div>
              )}

              <div className="erp-divider" />

              {/* Aktarım hedef özeti */}
              {targetFirmaKodu && (
                <div className="erp-target-summary">
                  <span className="erp-ts-label">Aktarım Hedefi ★</span>
                  <span className="erp-ts-val">
                    {targetFirmaAdi}
                  </span>
                  {targetSubeKodu && (
                    <span className="erp-ts-sub">
                      {targetSubeKodu}{targetSubeAdi ? ` — ${targetSubeAdi}` : ''}
                    </span>
                  )}
                  {(targetDonemLabel || targetDonemKodu) && (
                    <span className="erp-ts-sub" style={{ color: '#6b7280' }}>
                      {targetDonemLabel || targetDonemKodu}
                    </span>
                  )}
                </div>
              )}


              </div>
            </div>
          )}

        </div>{/* /.erp-main */}
      </div>{/* /.erp-body */}
    </div>
  );
}

export default App;
