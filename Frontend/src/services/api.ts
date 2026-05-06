import axios from 'axios';
import type {
  ISourceCompanyDto,
  ISourceBranchDto,
  ISourceDepotDto,
  ISourceCurrencyDto,
  ISourcePeriodDto,
  IDefaultSourceContextDto,
  IPoolContextDto,
  IInvoiceListRow,
  IInvoiceDetailDto,
  IInvoiceTransferRequestDto,
  IInvoiceTransferResultDto,
  ITargetResolveResultDto,
} from '../types';

const api = axios.create({
  // Dev'de Vite proxy (/api -> backend) kullanılır; prod'da aynı origin.
  baseURL: '/api',
  headers: { 'Content-Type': 'application/json' },
  // DİA/Backend dalgalanmasında UI'nin "sonsuz yükleniyor"da kalmasını önle.
  timeout: 60000,
});

const unwrapArray = <T>(data: any): T[] => {
  if (Array.isArray(data)) return data as T[];
  if (Array.isArray(data?.value)) return data.value as T[];
  if (Array.isArray(data?.Value)) return data.Value as T[];
  return [];
};

export const InvoiceService = {
  // ── Lookups (Real DİA) ───────────────────────────────────────────────────
  getDefaultSource: async (): Promise<IDefaultSourceContextDto> => {
    const res = await api.get('/lookups/default-source');
    return res.data;
  },

  getPool: async (): Promise<IPoolContextDto> => {
    const res = await api.get('/lookups/pool');
    return res.data;
  },

  getCompanies: async (forceRefresh = false): Promise<ISourceCompanyDto[]> => {
    const res = await api.get('/lookups/companies', {
      params: forceRefresh ? { _ts: Date.now() } : undefined,
    });
    const rows = unwrapArray<any>(res.data);
    return rows
      .map((r) => ({
        firma_kodu: Number(r.firma_kodu ?? r.firmaKodu ?? r.kodu ?? 0),
        firma_adi: String(r.firma_adi ?? r.firmaAdi ?? r.firmaadi ?? r.adi ?? '').trim(),
      }))
      .filter((r) => Number.isFinite(r.firma_kodu) && r.firma_kodu > 0);
  },

  getPeriods: async (firmaKodu: number): Promise<ISourcePeriodDto[]> => {
    const res = await api.get('/lookups/periods', { params: { firmaKodu } });
    const rows = unwrapArray<any>(res.data);
    return rows
      .map((r) => ({
        key: Number(r.key ?? r.Key ?? 0),
        donemkodu: Number(r.donemkodu ?? r.DonemKodu ?? r.donemKodu ?? 0),
        gorunenkod: String(r.gorunenkod ?? r.GorunenKod ?? r.gorunenKod ?? '').trim(),
        ontanimli: Boolean(r.ontanimli ?? r.Ontanimli ?? false),
        baslangic_tarihi: (r.baslangic_tarihi ?? r.BaslangicTarihi ?? '').toString().trim(),
        bitis_tarihi: (r.bitis_tarihi ?? r.BitisTarihi ?? '').toString().trim(),
      }))
      .filter((x) => Number.isFinite(x.key) && x.key > 0 && Number.isFinite(x.donemkodu) && x.donemkodu > 0);
  },

  getBranches: async (firmaKodu: number, donemKodu?: number): Promise<ISourceBranchDto[]> => {
    const res = await api.get('/lookups/branches', { params: { firmaKodu, donemKodu } });
    const rows = unwrapArray<any>(res.data);
    return rows
      .map((r) => ({
        key: Number(r.key ?? r.Key ?? 0),
        subeadi: String(r.subeadi ?? r.SubeAdi ?? r.subeAdi ?? r.adi ?? '').trim(),
      }))
      .filter((x) => Number.isFinite(x.key) && x.key > 0);
  },

  getBranchesAll: async (firmaKodu: number): Promise<ISourceBranchDto[]> => {
    const res = await api.get('/lookups/branches-all', { params: { firmaKodu } });
    const rows = unwrapArray<any>(res.data);
    return rows
      .map((r) => ({
        key: Number(r.key ?? r.Key ?? 0),
        subeadi: String(r.subeadi ?? r.SubeAdi ?? r.subeAdi ?? r.adi ?? '').trim(),
      }))
      .filter((x) => Number.isFinite(x.key) && x.key > 0);
  },

  getDepots: async (firmaKodu: number, subeKey: number, donemKodu?: number): Promise<ISourceDepotDto[]> => {
    const res = await api.get('/lookups/depots', { params: { firmaKodu, subeKey, donemKodu } });
    const rows = unwrapArray<any>(res.data);
    return rows
      .map((r) => ({
        key: Number(r.key ?? r.Key ?? 0),
        depoadi: String(r.depoadi ?? r.DepoAdi ?? r.depoAdi ?? r.adi ?? '').trim(),
      }))
      .filter((x) => Number.isFinite(x.key) && x.key > 0);
  },

  getDepotsAll: async (firmaKodu: number): Promise<ISourceDepotDto[]> => {
    const res = await api.get('/lookups/depots-all', { params: { firmaKodu } });
    const rows = unwrapArray<any>(res.data);
    return rows
      .map((r) => ({
        key: Number(r.key ?? r.Key ?? 0),
        depoadi: String(r.depoadi ?? r.DepoAdi ?? r.depoAdi ?? r.adi ?? '').trim(),
      }))
      .filter((x) => Number.isFinite(x.key) && x.key > 0);
  },

  resolveSubeDepoNames: async (req: { firmaKodu: number; donemKodu: number; subeKeys: number[]; depoKeys: number[] }): Promise<{ sube: Record<string, string>; depo: Record<string, string> }> => {
    const res = await api.post('/lookups/resolve-sube-depo-names', req);
    return res.data as { sube: Record<string, string>; depo: Record<string, string> };
  },

  resolveStokHizmet: async (req: { firmaKodu: number; donemKodu: number; fiyatKartKeys: number[] }): Promise<{ map: Record<string, { kodu: string; aciklama: string }> }> => {
    const res = await api.post('/lookups/resolve-stok-hizmet', req);
    return res.data as { map: Record<string, { kodu: string; aciklama: string }> };
  },

  resolveUnits: async (req: { firmaKodu: number; donemKodu: number; unitKeys: number[] }): Promise<{ map: Record<string, { kodu: string; adi: string }> }> => {
    const res = await api.post('/lookups/resolve-units', req);
    return res.data as { map: Record<string, { kodu: string; adi: string }> };
  },

  getCurrencies: async (firmaKodu: number, donemKodu: number): Promise<ISourceCurrencyDto[]> => {
    const res = await api.get('/lookups/currencies', { params: { firmaKodu, donemKodu } });
    const rows = unwrapArray<any>(res.data);
    return rows
      .map((r) => ({
        key: Number(r.key ?? r.Key ?? 0),
        kodu: String((r.kodu ?? r.Kodu ?? r.adi ?? r.Adi ?? '')).trim(),
        adi: String(r.adi ?? r.Adi ?? '').trim(),
      }))
      .filter((x) => Number.isFinite(x.key) && x.key > 0);
  },

  getInvoiceTypes: async (req: {
    firmaKodu: number;
    donemKodu: number;
    sourceSubeKey?: number | '';
    sourceDepoKey?: number | '';
  }): Promise<string[]> => {
    const res = await api.get('/lookups/invoice-types', {
      params: {
        firmaKodu: req.firmaKodu,
        donemKodu: req.donemKodu,
        sourceSubeKey: req.sourceSubeKey === '' ? undefined : req.sourceSubeKey,
        sourceDepoKey: req.sourceDepoKey === '' ? undefined : req.sourceDepoKey,
      },
    });
    return res.data as string[];
  },

  resolveTarget: async (req: {
    targetFirmaKodu: number;
    sourceDonemKodu?: number;
    sourceInvoiceDate?: string;
  }): Promise<ITargetResolveResultDto> => {
    const res = await api.post('/lookups/resolve-target', req);
    return res.data;
  },

  getTransferFlags: async (): Promise<{
    transferRawMode: boolean;
    transferConcurrency?: number;
    transferBatchSize?: number;
  }> => {
    const res = await api.get('/lookups/transfer-flags');
    const d = res.data ?? {};
    return {
      transferRawMode: Boolean(d.transferRawMode),
      transferConcurrency:
        d.transferConcurrency != null ? Number(d.transferConcurrency) : undefined,
      transferBatchSize: d.transferBatchSize != null ? Number(d.transferBatchSize) : undefined,
    };
  },

  resolveTargetCariKey: async (
    firmaKodu: number,
    donemKodu: number,
    cariKodu: string
  ): Promise<{ key: number | null }> => {
    const res = await api.get('/lookups/resolve-target-cari-key', {
      params: { firmaKodu, donemKodu, cariKodu },
    });
    const k = res.data?.key;
    const n = k != null ? Number(k) : NaN;
    return { key: Number.isFinite(n) && n > 0 ? n : null };
  },

  resolveTargetDovizKey: async (
    firmaKodu: number,
    donemKodu: number,
    dovizKodu: string
  ): Promise<{ key: number | null }> => {
    const res = await api.get('/lookups/resolve-target-doviz-key', {
      params: { firmaKodu, donemKodu, dovizKodu },
    });
    const k = res.data?.key;
    const n = k != null ? Number(k) : NaN;
    return { key: Number.isFinite(n) && n > 0 ? n : null };
  },

  /** RAW satır: birim + kalem türü listesi — transfer başına 1 HTTP (backend’de 2 DİA listesi paralel). */
  getRawLineLookups: async (
    firmaKodu: number,
    donemKodu: number
  ): Promise<{ birimler: Array<{ key: number; kod: string }>; kalemTurleri: Array<{ key: number; kod: string }> }> => {
    const res = await api.get('/lookups/raw-line-lookups', { params: { firmaKodu, donemKodu } });
    const d = res.data ?? {};
    const birimler = Array.isArray(d.birimler) ? d.birimler : [];
    const kalemTurleri = Array.isArray(d.kalemTurleri) ? d.kalemTurleri : [];
    return { birimler, kalemTurleri };
  },

  // ── Üst grid: scf_fatura_listele ─────────────────────────────────────────
  listInvoices: async (req: {
    firma_kodu: number;
    donem_kodu: number;
    source_sube_key?: number;
    source_depo_key?: number;
    filters?: string;
    ust_islem_turu_key?: number;
    only_distributable?: boolean;
    only_non_distributable?: boolean;
    limit: number;
    offset: number;
  }): Promise<IInvoiceListRow[]> => {
    const res = await api.post('/invoices/list', req);
    return res.data;
  },

  // ── Alt grid: scf_fatura_getir.result.m_kalemler ─────────────────────────
  getInvoice: async (key: number, params: { firmaKodu: number; donemKodu: number }): Promise<{ data: IInvoiceDetailDto; status: number }> => {
    const res = await api.get(`/invoices/${key}`, { params });
    return { data: res.data, status: res.status };
  },

  transferInvoice: async (req: IInvoiceTransferRequestDto): Promise<IInvoiceTransferResultDto> => {
    // Aktarım sırasında DIA tarafında birden fazla lookup yapılabildiği için daha uzun timeout gerekir.
    const res = await api.post('/invoices/transfer', req, { timeout: 180000 });
    return res.data;
  },

  clearTransferState: async (req: { sourceInvoiceKey?: number; sourceLineKey?: number }): Promise<{ cleared: number }> => {
    const res = await api.post('/diag/clear-transfer-state', req);
    return res.data as { cleared: number };
  },

  // ── Özel Rapor: RPR000000004 (rpr_raporsonuc_getir) ───────────────────────
  faturaGetirRaw: async (filtre: {
    firma_kodu?: number;
    donem_kodu?: number;
    report_code?: string;
    baslangic?: string;
    bitis?: string;
    fatura_tipi?: string;
    kaynak_sube?: number;
    kaynak_depo?: number;
    ust_islem?: string;
    cari_adi?: string;
    fatura_no?: string;
    fatura_turu?: string;
    kalem_sube?: string;
    force_refresh?: boolean;
  }): Promise<any> => {
    const res = await api.post('/fatura-getir', filtre);
    if (res.data && typeof res.data === 'object' && res.data.success === false) {
      const msg = String(res.data.message ?? 'Rapor çağrısı başarısız.');
      throw new Error(msg);
    }
    return res.data;
  },

  faturaGetir: async (filtre: {
    firma_kodu?: number;
    donem_kodu?: number;
    report_code?: string;
    baslangic?: string;
    bitis?: string;
    fatura_tipi?: string;
    kaynak_sube?: number;
    kaynak_depo?: number;
    ust_islem?: string;
    cari_adi?: string;
    fatura_no?: string;
    fatura_turu?: string;
    kalem_sube?: string;
  }): Promise<any[]> => {
    const res = await api.post('/fatura-getir', filtre);
    // Backend bazı hatalarda 200 dönüp {success:false,message:"..."} gönderebilir.
    // Bu durumda UI "boş veri" sanmasın; hata fırlat.
    if (res.data && typeof res.data === 'object' && res.data.success === false) {
      const msg = String(res.data.message ?? 'Rapor çağrısı başarısız.');
      throw new Error(msg);
    }
    return unwrapArray<any>(res.data?.data ?? res.data);
  },

  faturaAktar: async (req: {
    sourceFirmaKodu: number;
    sourceDonemKodu: number;
    sourceSubeKey?: number | '';
    sourceDepoKey?: number | '';
    targetFirmaKodu: number;
    targetDonemKodu: number;
    targetSubeKey: number;
    targetDepoKey: number;
    invoices: Array<{
      sourceInvoiceKey: number;
      selectedKalemKeys: number[];
      headerSnapshot?: Record<string, unknown>;
      selectedLineSnapshots?: any[];
    }>;
  }, opts?: { signal?: AbortSignal }): Promise<{
    success: boolean;
    total?: number;
    successCount?: number;
    failedCount?: number;
    durationMs?: number;
    results: Array<{ sourceInvoiceKey: number; result: any }>;
  }> => {
    const payload = {
      ...req,
      sourceSubeKey: req.sourceSubeKey === '' ? undefined : req.sourceSubeKey,
      sourceDepoKey: req.sourceDepoKey === '' ? undefined : req.sourceDepoKey,
    };
    const res = await api.post('/fatura-aktar', payload, { timeout: 600000, signal: opts?.signal });
    return res.data as {
      success: boolean;
      total?: number;
      successCount?: number;
      failedCount?: number;
      durationMs?: number;
      results: Array<{ sourceInvoiceKey: number; result: any }>;
    };
  },

  transferLog: async (items: Array<{ invoiceKey: number; lineCount: number; targetFirma: number; success: boolean; errorMessage?: string; timestamp: string }>) => {
    const res = await api.post('/transfer/log', { items });
    return res.data as { success: boolean; written: number };
  },

  getRecentTransferLogs: async (limit = 500) => {
    const res = await api.get('/transfer/log/recent', { params: { limit } });
    return res.data as { items: Array<{ invoiceKey: number; lineCount: number; targetFirma: number; success: boolean; errorMessage?: string; timestamp: string }> };
  },
};

