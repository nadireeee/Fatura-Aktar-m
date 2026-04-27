import axios from 'axios';
import type {
  ISourceCompanyDto,
  ISourceBranchDto,
  ISourceDepotDto,
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

  getBranches: async (firmaKodu: number): Promise<ISourceBranchDto[]> => {
    const res = await api.get('/lookups/branches', { params: { firmaKodu } });
    const rows = unwrapArray<any>(res.data);
    return rows
      .map((r) => ({
        key: Number(r.key ?? r.Key ?? 0),
        subeadi: String(r.subeadi ?? r.SubeAdi ?? r.subeAdi ?? r.adi ?? '').trim(),
      }))
      .filter((x) => Number.isFinite(x.key) && x.key > 0);
  },

  getDepots: async (firmaKodu: number, subeKey: number): Promise<ISourceDepotDto[]> => {
    const res = await api.get('/lookups/depots', { params: { firmaKodu, subeKey } });
    const rows = unwrapArray<any>(res.data);
    return rows
      .map((r) => ({
        key: Number(r.key ?? r.Key ?? 0),
        depoadi: String(r.depoadi ?? r.DepoAdi ?? r.depoAdi ?? r.adi ?? '').trim(),
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
};

