type AnyObj = Record<string, any>;

const str = (v: any) => String(v ?? '').trim();
const num = (v: any) => {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
};

const INVOICE_TYPE_LABEL_BY_CODE: Record<string, string> = {
  '1': 'Mal Alım',
  '2': 'Perakende Satış',
  '3': 'Toptan Satış',
  '4': 'Alınan Hizmet',
  '5': 'Verilen Hizmet',
  '6': 'Alım İade',
  '7': 'Perakende Satış İade',
  '8': 'Toptan Satış İade',
  '9': 'Alınan Fiyat Farkı',
  '10': 'Verilen Fiyat Farkı',
  '15': 'Müstahsil Makbuzu',
};

const normLabel = (s: string) => s.trim().toLowerCase().replace(/\s+/g, ' ');

/** Raporda sadece tür adı varsa (sayı yok) backend invoiceTypeCode için sayı üretir. */
function resolveInvoiceTypeCode(r: AnyObj, invoiceTypeLabel: string, invoiceTypeRaw: unknown): number {
  const tryNum = (v: unknown) => {
    if (v == null || v === '') return 0;
    if (typeof v === 'number' && Number.isFinite(v) && v > 0) return v;
    const n = Number(String(v).trim());
    return Number.isFinite(n) && n > 0 ? n : 0;
  };
  const fromAlias = tryNum(
    r?.snapshot_invoice_type_num ?? r?.invoiceTypeCode ?? r?.invoice_type_code ?? r?.InvoiceTypeCode
  );
  if (fromAlias > 0) return fromAlias;
  for (const k of [
    'turu',
    'TURU',
    'fatura_turu',
    'FATURA_TURU',
    'tur',
    'TUR',
    'fat_turu',
    'FAT_TURU',
    'fatturu',
    'FATURATURU',
    'fk.turu',
    'fk.TURU',
    'fk.fatura_turu',
  ]) {
    const n = tryNum(r?.[k]);
    if (n > 0) return n;
  }
  const fromRaw = tryNum(invoiceTypeRaw);
  if (fromRaw > 0) return fromRaw;
  const k = normLabel(invoiceTypeLabel);
  if (!k || k === 'bilinmeyen') return 0;
  for (const [codeStr, lbl] of Object.entries(INVOICE_TYPE_LABEL_BY_CODE)) {
    if (normLabel(lbl) === k) return Number(codeStr);
  }
  for (const [codeStr, lbl] of Object.entries(INVOICE_TYPE_LABEL_BY_CODE)) {
    const L = normLabel(lbl);
    if (!L) continue;
    if (k === L || k.includes(L) || L.includes(k)) return Number(codeStr);
  }
  return 0;
}

const pickStr = (o: AnyObj, keys: string[]) => {
  for (const k of keys) {
    const s = str(o?.[k]);
    if (s) return s;
  }
  return '';
};

const normalizeDynBranch = (s: unknown) => {
  let x = str(s);
  if (!x) return '';
  // NBSP / invisible boşluklar
  x = x.replace(/[\u200B-\u200D\uFEFF]/g, '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
  const u = x.toUpperCase();
  if (!x) return '';
  // "---" gibi placeholder'lar: boş kabul et
  if (/^[-—–]+$/.test(x)) return '';
  if (u === '0' || u === 'NULL' || u === 'UNDEFINED' || u === 'NONE' || u === 'N/A') return '';
  return x;
};

const pickNum = (o: AnyObj, keys: string[]) => {
  for (const k of keys) {
    const v = o?.[k];
    const n = Number(v);
    if (Number.isFinite(n) && n !== 0) return n;
  }
  // allow explicit zeros
  for (const k of keys) {
    const v = o?.[k];
    if (v === 0 || v === '0') return 0;
  }
  return 0;
};

/** İlk tanımlı sayısal alan (0 dahil). */
const pickNumDefined = (o: AnyObj, keys: string[]) => {
  for (const k of keys) {
    if (!Object.prototype.hasOwnProperty.call(o ?? {}, k)) continue;
    const v = o?.[k];
    if (v === '' || v === null || v === undefined) continue;
    const n = Number(v);
    if (Number.isFinite(n)) return n;
  }
  return undefined as undefined | number;
};

/**
 * DİA / bazı SQL çıktıları KDV oranını 20 yerine 20000000 (mikro) veya tutarı `kdv` kolonunda döndürebilir.
 * Aktarımda scf_fatura_ekle.kdv alanına yalnız 0..100 gitmeli.
 */
function resolveLineKdvPercentAndTutar(
  r: AnyObj,
  lineTotal: number,
  lineKdvTutariRaw: number
): { pct: number; lineKdvTutari: number } {
  const kdvtutari =
    lineKdvTutariRaw > 0
      ? lineKdvTutariRaw
      : num(
          r?.kdvtutari ??
            r?.KDV_TUTARI ??
            r?.kdv_tutari ??
            r?.kdvtutar ??
            r?.KDVTUTAR ??
            r?.kdvduzentutari ??
            0
        );

  const explicitPct = pickNumDefined(r, [
    'kdv_oran',
    'KDV_ORAN',
    'kdvorani',
    'KDVORANI',
    'kdvyuzde',
    'KDVYUZDE',
    'kdvyuzdesi',
    'KDVYUZDESI',
    'kdv_yuzde',
    'KDV_YUZDE',
    'kalemkdvyuzde',
    'oran',
    'ORAN',
    'vergipntr',
    'VERGIPNTR',
  ]);

  const tryMillion = (v: number): number | undefined => {
    if (!Number.isFinite(v) || v <= 0) return undefined;
    const m1 = v / 1_000_000;
    if (m1 > 0.01 && m1 <= 100) return Math.round(m1 * 10_000) / 10_000;
    const m2 = v / 100_000;
    if (m2 > 0.01 && m2 <= 100) return Math.round(m2 * 10_000) / 10_000;
    return undefined;
  };

  if (explicitPct !== undefined) {
    const ep = explicitPct;
    if (ep >= 0 && ep <= 100) return { pct: ep, lineKdvTutari: kdvtutari };
    const mm = tryMillion(ep);
    if (mm !== undefined) return { pct: mm, lineKdvTutari: kdvtutari };
  }

  const ambiguous = pickNumDefined(r, ['kdv', 'KDV', 'kalemkdv', 'KALEMKDV']);
  const amb = ambiguous ?? 0;

  if (amb > 0 && amb <= 100) return { pct: amb, lineKdvTutari: kdvtutari };

  if (amb > 100) {
    const mm = tryMillion(amb);
    if (mm !== undefined) return { pct: mm, lineKdvTutari: kdvtutari };
    if (lineTotal > 0 && kdvtutari > 0 && Math.abs(amb - kdvtutari) < 0.02) {
      const p = (kdvtutari / lineTotal) * 100;
      if (p > 0 && p <= 100.01) return { pct: Math.round(p * 100) / 100, lineKdvTutari: kdvtutari };
    }
    if (lineTotal > 0) {
      const p2 = (amb / lineTotal) * 100;
      if (p2 > 0.01 && p2 <= 100.01) return { pct: Math.round(p2 * 100) / 100, lineKdvTutari: amb };
    }
  }

  if (lineTotal > 0 && kdvtutari > 0) {
    const p = (kdvtutari / lineTotal) * 100;
    if (p > 0 && p <= 100.01) return { pct: Math.round(p * 100) / 100, lineKdvTutari: kdvtutari };
  }

  return { pct: 0, lineKdvTutari: kdvtutari };
}

export type NormalizedRprRow = {
  sourceRow: AnyObj;

  invalid: boolean;

  invoiceKey: number;
  lineKey: number;

  invoiceNo: string;
  fisNo: string;
  date: string;

  invoiceTypeRaw: any;
  invoiceTypeLabel: string;

  upperProcessCode: string;
  upperProcessName: string;

  cariKey: number;
  /** Cari kart kodu — snapshot / aktarım için (ünvan değil). */
  cariCode: string;
  cariName: string;

  /** DİA fatura türü sayısal kodu (>0); raporda yoksa etiketten çözülür. */
  invoiceTypeCode: number;

  sourceBranchKey: number;
  sourceBranchName: string;
  sourceWarehouseKey: number;
  sourceWarehouseName: string;

  currencyKey: number;
  /** Kalem satırı para birimi (fatura dövizinden farklı olabilir). */
  currencyCode: string;
  /** Fatura başlığı para birimi — snapshot başlığı için. */
  invoiceCurrencyCode: string;
  exchangeRate: number;
  /** Fatura başlığı döviz kuru (havuz). */
  invoiceExchangeRate: number;

  /** Yerel / son birim fiyat (dövizli faturalarda DİA ile uyum). */
  yerelBirimFiyati: number;
  sonBirimFiyati: number;

  invoiceTotal: number;
  invoiceNet: number;
  invoiceVat: number;
  invoiceDiscountTotal: number;
  invoiceExpenseTotal: number;

  lineTypeRaw: any;
  lineTypeLabel: string;

  itemCode: string;
  itemName: string;

  unitName: string;
  quantity: number;
  unitPrice: number;

  lineTotal: number;
  lineDiscount: number;
  lineExpense: number;

  /** Kalem KDV oranı (%) — snapshot doğrulaması için */
  lineKdvPercent: number;

  /** Satır KDV tutarı (kdvtutari) — oran türetmek için */
  lineKdvTutari: number;

  dynamicBranch: string;

  transferStatus: string;
  transferFingerprint: string;

  /** Aktarım snapshot’ı (IsValidSnapshot) için alanlar tam mı — eksikse aktarılamaz. */
  snapshotReady: boolean;
  snapshotIssues: string[];
  /** SQL snapshot_error (örn. "TURU CARI DOVIZ") — toplu kalite; tooltip / liste */
  snapshotErrorSummary: string;
};

export function normalizeRprRow(
  r: AnyObj,
  ctx: {
    currencyFallbackCode?: string;
    resolveBranchName?: (k: number) => string;
    resolveWarehouseName?: (k: number) => string;
    resolveCurrencyCode?: (k: number) => string;
    // fingerprint parts may be empty in UI; still build stable key
    targetFirmKey?: number | string;
    targetBranchKey?: number | string;
    targetWarehouseKey?: number | string;
    targetPeriodKey?: number | string;
  }
): NormalizedRprRow {
  const invoiceKey = pickNum(r, ['fatura_key', 'FATURA_KEY', '_key_scf_fatura', '_key', 'key']);
  const lineKey = pickNum(r, ['kalem_key', 'KALEM_KEY', '_key', 'key']);

  const invoiceNo = pickStr(r, ['fatura_no', 'FATURA_NO', 'belgeno2', 'BELGENO2', 'belgeno', 'BELGENO', 'fisno', 'FISNO']);
  const fisNo = pickStr(r, ['fisno', 'FISNO', 'fis_no', 'FIS_NO']);
  const date = pickStr(r, [
    'snapshot_iso_date',
    'SNAPSHOT_ISO_DATE',
    'date',
    'DATE',
    'tarih',
    'TARIH',
    'tarih_saat',
    'TARIH_SAAT',
    'fatura_tarihi',
    'FATURA_TARIHI',
    'islemtarihi',
    'ISLEMTARIHI',
    'belge_tarihi',
    'BELGE_TARIHI',
    'evrak_tarihi',
    'EVRAK_TARIHI',
    'ftarih',
    'FTARIH',
    'fatura_tarih',
    'fk.tarih',
    'fk.TARIH',
  ]);

  const invoiceTypeRaw = r?.turu ?? r?.TURU ?? r?.tur ?? r?.TUR ?? r?.fatura_turu ?? r?.FATURA_TURU;
  const invoiceTypeFromReport = pickStr(r, [
    'turuack',
    'TURUACK',
    'turu_ack',
    'invoice_type_label',
    'fatura_turu_adi',
    'FATURA_TURU_ADI',
  ]);
  const invoiceTypeRawStr = str(invoiceTypeRaw);
  const invoiceTypeLabel =
    invoiceTypeFromReport ||
    INVOICE_TYPE_LABEL_BY_CODE[invoiceTypeRawStr] ||
    INVOICE_TYPE_LABEL_BY_CODE[str(r?.fatura_turu)] ||
    INVOICE_TYPE_LABEL_BY_CODE[str(r?.FATURA_TURU)] ||
    invoiceTypeRawStr ||
    'Bilinmeyen';

  const invoiceTypeCode = resolveInvoiceTypeCode(r, invoiceTypeLabel, invoiceTypeRaw);

  // Üst işlem: rapora göre bazı satırlarda farklı kolon adlarıyla gelebiliyor.
  const upperProcessCode = pickStr(r, ['ust_islem_kodu', 'UST_ISLEM_KODU', 'ustislemkodu', 'ust_islem', 'UST_ISLEM', 'ustislem']);
  const upperProcessName = pickStr(r, ['ust_islem_aciklama', 'UST_ISLEM_ACIKLAMA', 'ustislemaciklama', 'ust_islem', 'UST_ISLEM', 'ustislem']);

  const cariKey = pickNum(r, ['_key_scf_carikart', '_KEY_SCF_CARIKART', 'cari_key', 'CARI_KEY']);
  const cariName = pickStr(r, ['cari_adi', 'CARI_ADI', 'cari_unvan', 'cariunvan', 'unvan', '__cariunvan']);
  const cariCode = pickStr(r, [
    'snapshot_cari_kodu',
    'SNAPSHOT_CARI_KODU',
    'cariCode',
    'CARICODE',
    'carikartkodu',
    'CARIKARTKODU',
    '__carikartkodu',
    'carikodu',
    'CARIKODU',
    'cari_kodu',
    'CARI_KODU',
    'cari_kart_kodu',
    'CARIKART_KODU',
    'carikart_kodu',
    'musteri_kodu',
    'MUSTERI_KODU',
    'hesapkodu',
    'HESAPKODU',
    'cari_hesap_kodu',
    'fk.carikartkodu',
    'fk.CARIKARTKODU',
    'fk.carikodu',
    'fk.CARIKODU',
  ]);

  const sourceBranchKey = pickNum(r, [
    // RPR SQL alias (COALESCE kaynak şube anahtarı)
    'kaynak_sube',
    'KAYNAK_SUBE',
    '_key_sis_sube_source',
    '_KEY_SIS_SUBE_SOURCE',
    '_key_sis_sube_dest',
    '_KEY_SIS_SUBE_DEST',
    // Bazı şemalarda tek şube anahtarı
    '_key_sis_sube',
    '_KEY_SIS_SUBE',
    'source_sube',
    'SOURCE_SUBE',
    'fk._key_sis_sube_source',
    'fk._key_sis_sube_dest',
    'f._key_sis_sube',
  ]);
  const sourceWarehouseKey = pickNum(r, [
    '_key_sis_depo_source',
    '_KEY_SIS_DEPO_SOURCE',
    '_key_sis_depo_dest',
    '_KEY_SIS_DEPO_DEST',
    'kaynak_depo',
    'KAYNAK_DEPO',
    'source_depo',
    'SOURCE_DEPO',
    'fk._key_sis_depo_source',
    'fk._key_sis_depo_dest',
  ]);

  const sourceBranchName =
    pickStr(r, [
      'kaynak_sube_adi',
      'KAYNAK_SUBE_ADI',
      'sube_adi',
      'SUBE_ADI',
      'subeadi',
      'sube',
      'source_sube_adi',
      '_key_sis_sube_source_text',
      '_KEY_SIS_SUBE_SOURCE_TEXT',
      '_key_sis_sube_dest_text',
      '_KEY_SIS_SUBE_DEST_TEXT',
    ]) ||
    (ctx.resolveBranchName ? ctx.resolveBranchName(sourceBranchKey) : '');
  const sourceWarehouseName =
    pickStr(r, [
      'kaynak_depo_adi',
      'KAYNAK_DEPO_ADI',
      'depo_adi',
      'DEPO_ADI',
      'depoadi',
      'depo',
      'source_depo_adi',
      '_key_sis_depo_source_text',
      '_KEY_SIS_DEPO_SOURCE_TEXT',
    ]) ||
    (ctx.resolveWarehouseName ? ctx.resolveWarehouseName(sourceWarehouseKey) : '');

  const currencyKey = pickNum(r, [
    'kalem_doviz_key',
    'KALEM_DOVIZ_KEY',
    'fatura_doviz_key',
    'FATURA_DOVIZ_KEY',
    'doviz_key',
    'DOVIZ_KEY',
    '_key_sis_doviz',
    '_KEY_SIS_DOVIZ',
  ]);

  const invoiceCurrencyCode =
    pickStr(r, [
      'fatura_doviz_kodu',
      'FATURA_DOVIZ_KODU',
      'anadovizkodu',
      'ANADOIVIZKODU',
      'snapshot_currency_kodu',
      'SNAPSHOT_CURRENCY_KODU',
      '__anadovizadi',
      'rapor_para',
      'RAPORDOVIZ',
      'dovizkodu',
      'DOVIZKODU',
      'dovizadi',
      'doviz',
      'currencyCode',
      'CURRENCYCODE',
      'para_birimi',
      'PARA_BIRIMI',
      'fk.dovizkodu',
      'fk.DOVIZKODU',
    ]) ||
    (ctx.resolveCurrencyCode
      ? ctx.resolveCurrencyCode(pickNum(r, ['fatura_doviz_key', 'FATURA_DOVIZ_KEY', 'doviz_key', 'DOVIZ_KEY']))
      : '') ||
    (ctx.currencyFallbackCode ?? '');

  const lineOnlyCurrency = pickStr(r, [
    'kalem_doviz_kodu',
    'KALEM_DOVIZ_KODU',
    'kalemdovizkodu',
    'KALEMDOVIZKODU',
    'satir_doviz_kodu',
    'SATIR_DOVIZ_KODU',
    'kalem_doviz',
    'KALEM_DOVIZ',
    'satir_doviz',
    'SATIR_DOVIZ',
  ]);

  const lineCurrencyCode = lineOnlyCurrency || invoiceCurrencyCode;

  const invoiceExchangeRate = num(
    r?.fatura_doviz_kur ?? r?.FATURA_DOVIZ_KUR ?? r?.dovizkuru ?? r?.DOVIZKURU ?? 0
  );
  const lineExchangeRate = num(r?.kalem_doviz_kur ?? r?.KALEM_DOVIZ_KUR ?? r?.kalemdovizkur ?? r?.KALEMDOVIZKUR ?? 0);
  let exchangeRate = lineExchangeRate > 0 ? lineExchangeRate : invoiceExchangeRate;
  if (!(exchangeRate > 0)) exchangeRate = num(r?.dovizkuru ?? r?.DOVIZKURU ?? r?.kalem_doviz_kur ?? 0);

  const currencyCode =
    lineCurrencyCode ||
    (ctx.resolveCurrencyCode ? ctx.resolveCurrencyCode(currencyKey) : '') ||
    (ctx.currencyFallbackCode ?? '');

  const invoiceTotal = num(r?.geneltoplam ?? r?.GENELTOPLAM ?? r?.toplam ?? r?.TOPLAM ?? 0);
  const invoiceVat = num(r?.toplamkdv ?? r?.TOPLAMKDV ?? r?.kdvtutari ?? r?.kdv_tutari ?? 0);
  const invoiceDiscountTotal = num(r?.toplamindirim ?? r?.indirim_tutar ?? r?.indirimtutari ?? 0);
  const invoiceExpenseTotal = num(r?.toplammasraf ?? r?.masraf_tutar ?? r?.masraftutari ?? 0);

  let invoiceNet = pickNum(r, [
    'fatura_net',
    'FATURA_NET',
    'fatura_net_tutari',
    'FATURA_NET_TUTARI',
    'net',
    'NET',
  ]);
  // Bazı RPR satırlarında `net` kolonu fiş neti değil; genel toplam + KDV ile tutarsız büyük değer dönebiliyor.
  if (invoiceTotal > 0 && invoiceNet > invoiceTotal * 20 && invoiceVat >= 0 && invoiceVat < invoiceTotal) {
    const approxNet = invoiceTotal - invoiceVat;
    if (approxNet > 0 && approxNet <= invoiceTotal) invoiceNet = approxNet;
  }

  const lineTypeRaw = r?.kalemturu ?? r?.KALEMTURU ?? r?._key_kalemturu ?? r?._key_kalemturu;
  const lineTypeLabel = (() => {
    const t = str(lineTypeRaw);
    if (t === 'MLZM') return 'STOK';
    if (t === 'HZMT') return 'HIZMET';
    const fromReport = pickStr(r, ['kalem_tipi', 'KALEM_TIPI']);
    return fromReport || t;
  })();

  const itemCode = pickStr(r, ['stok_hizmet_kodu', 'stokhizmetkodu', 'kod', 'stokkartkodu', 'hizmetkartkodu']);
  const itemName = pickStr(r, ['stok_hizmet_adi', 'stokhizmetadi', 'stok_hizmet_aciklama', 'aciklama', 'adi']);

  const unitName = pickStr(r, ['birim_adi', 'birimadi', 'birimkodu', 'anabirimadi', 'birim']);
  const quantity = num(r?.miktar ?? r?.anamiktar ?? 0);
  const unitPrice = num(r?.birimfiyati ?? r?.birim_fiyati ?? r?.sonbirimfiyati ?? 0);
  const yerelBf = num(
    r?.yerelbirimfiyati ??
      r?.YERELBIRIMFIYATI ??
      r?.yerel_birim_fiyati ??
      r?.yerelbirimfiyat ??
      r?.YEREL_BIRIM_FIYAT ??
      0
  );
  const sonBf = num(r?.sonbirimfiyati ?? r?.SONBIRIMFIYATI ?? r?.son_birim_fiyat ?? 0);
  const yerelBirimFiyati = yerelBf > 0 ? yerelBf : unitPrice;
  const sonBirimFiyati = sonBf > 0 ? sonBf : unitPrice;

  const lineTotal = num(r?.tutari ?? r?.tutar ?? 0);
  const lineDiscount = num(r?.indirimtutari ?? r?.indirim_tutar ?? r?.indirimtoplam ?? 0);
  const lineExpense = num(r?.masraftutari ?? r?.masraf_tutar ?? r?.toplammasraf ?? 0);
  const lineKdvTutariPre = num(
    r?.kdvtutari ??
      r?.KDV_TUTARI ??
      r?.kdv_tutari ??
      r?.kdvtutar ??
      r?.KDVTUTAR ??
      r?.kdvduzentutari ??
      0
  );
  const { pct: lineKdvPercent, lineKdvTutari } = resolveLineKdvPercentAndTutar(r, lineTotal, lineKdvTutariPre);

  // Dağıtılacak ekranı için kritik: "kalem şube" / dinamik hedef şube.
  // Not: kaynak şubeyi fallback yapmak yanıltıcı olabilir; o yüzden sadece kalem/dinamik alanları kullanıyoruz.
  const dynamicBranch = normalizeDynBranch(pickStr(r, [
    // SQL alias verilmediyse noktalı isim gelebilir
    'fk.__dinamik__fatsube',
    'fk.__DINAMIK__FATSUBE',
    '__dinamik__fatsube',
    '__DINAMIK__FATSUBE',
    'fatsube',
    'FATSUBE',
    'kalem_sube',
    'KALEM_SUBE',
    'kalem_sube_adi',
    'KALEM_SUBE_ADI',
    'kalemsube',
    'KALEM_SUBE_TEXT',
  ]));

  const transferStatus = pickStr(r, ['aktarim_durumu', 'aktarim_durumu_aciklama', 'transfer_status']) || 'Bekliyor';
  const fp = `${invoiceKey}:${lineKey}:${str(ctx.targetFirmKey)}:${str(ctx.targetBranchKey)}:${str(ctx.targetWarehouseKey)}:${str(ctx.targetPeriodKey)}`;

  // ERP gerçeği: bazı rapor satırlarında cari/stok/kalem tipi boş olabilir.
  // Dağıtılacak/Tüm listeyi "boş" göstermemek için invalid'i sadece anahtar validasyonuna indiriyoruz.
  const invalid = !(Number.isFinite(invoiceKey) && invoiceKey > 0) || !(Number.isFinite(lineKey) && lineKey > 0);

  const snapshotErrorSummary = pickStr(r, ['snapshot_error', 'SNAPSHOT_ERROR']).trim();

  const issueSet = new Set<string>();
  if (snapshotErrorSummary) {
    for (const w of snapshotErrorSummary.split(/\s+/)) {
      const u = w.trim().toUpperCase();
      if (u === 'TURU') issueSet.add('invoiceTypeCode');
      else if (u === 'CARI') issueSet.add('cariCode');
      else if (u === 'DATE') issueSet.add('date');
      else if (u === 'DOVIZ') issueSet.add('currencyCode');
    }
  }
  if (!String(cariCode ?? '').trim()) issueSet.add('cariCode');
  if (!String(date ?? '').trim()) issueSet.add('date');
  if (!(Number.isFinite(invoiceTypeCode) && invoiceTypeCode > 0)) issueSet.add('invoiceTypeCode');
  if (!String(currencyCode ?? '').trim()) issueSet.add('currencyCode');
  const snapshotIssues = Array.from(issueSet);
  const snapshotReady = !invalid && snapshotIssues.length === 0;

  if (invalid) {
    // eslint-disable-next-line no-console
    console.error('[RPR normalize] invalid row', {
      invoiceKey,
      lineKey,
      itemCode,
      lineTypeLabel,
      cariName,
      invoiceNo,
      date,
      sourceRowKeys: Object.keys(r ?? {}).slice(0, 50),
    });
  }

  return {
    sourceRow: r,
    invalid,
    invoiceKey,
    lineKey,
    invoiceNo,
    fisNo,
    date,
    invoiceTypeRaw,
    invoiceTypeLabel,
    upperProcessCode,
    upperProcessName,
    cariKey,
    cariCode,
    cariName,
    invoiceTypeCode,
    sourceBranchKey,
    sourceBranchName,
    sourceWarehouseKey,
    sourceWarehouseName,
    currencyKey,
    currencyCode,
    invoiceCurrencyCode,
    exchangeRate,
    invoiceExchangeRate,
    yerelBirimFiyati,
    sonBirimFiyati,
    invoiceTotal,
    invoiceNet,
    invoiceVat,
    invoiceDiscountTotal,
    invoiceExpenseTotal,
    lineTypeRaw,
    lineTypeLabel,
    itemCode,
    itemName,
    unitName,
    quantity,
    unitPrice,
    lineTotal,
    lineDiscount,
    lineExpense,
    lineKdvPercent,
    lineKdvTutari,
    dynamicBranch,
    transferStatus,
    transferFingerprint: fp,
    snapshotReady,
    snapshotIssues,
    snapshotErrorSummary,
  };
}

