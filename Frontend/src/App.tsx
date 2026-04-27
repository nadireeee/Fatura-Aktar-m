import { useEffect, useState, useMemo, useCallback, useRef, type MouseEvent } from 'react';
import { InvoiceService } from './services/api';
import type {
  IInvoiceListRow,
  IInvoiceLineListRow,
  ITransferLogEntry,
  ISourceCompanyDto,
  ISourceBranchDto,
  ISourceDepotDto,
  ISourcePeriodDto,
  TransferStatus,
} from './types';
import './index.css';

type TransferTypeFilter = 'hepsi' | 'tum_faturalar' | 'dagitilacak_faturalar';
type UstIslemTuruFilter = '' | 'A' | 'B';

type ViewerTabState = {
  id: string;
  firmaKodu: number;
  firmaAdi: string;
  donemKodu: number;
  invoices: IInvoiceListRow[];
  loading: boolean;
  offset: number;
  hasMore: boolean;
};

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

  // ── Havuz context lookups ────────────────────────────────────────────────
  const [companies, setCompanies] = useState<ISourceCompanyDto[]>([]);
  const [periods, setPeriods] = useState<ISourcePeriodDto[]>([]);
  const [branches, setBranches] = useState<ISourceBranchDto[]>([]);
  const [sourceDepots, setSourceDepots] = useState<ISourceDepotDto[]>([]);

  const [poolFirmaKodu, setPoolFirmaKodu] = useState<number>(0);
  const [poolFirmaAdi, setPoolFirmaAdi] = useState<string>('');
  const [defaultSourceDonemKodu, setDefaultSourceDonemKodu] = useState<number>(0);
  const [sourceDonemKodu, setSourceDonemKodu] = useState<number>(0);
  const [sourceSubeKey, setSourceSubeKey] = useState<number | ''>(''); // örnek: 2333
  const [sourceDepoKey, setSourceDepoKey] = useState<number | ''>('');

  // ── Fatura verileri ───────────────────────────────────────────────────────
  const [invoices,   setInvoices]   = useState<IInvoiceListRow[]>([]);
  const [lines,      setLines]      = useState<IInvoiceLineListRow[]>([]);
  const [lineSnapshotsByInvoice, setLineSnapshotsByInvoice] = useState<Record<number, any[]>>({});
  const [loading,    setLoading]    = useState(false);
  const [linesLoading, setLinesLoading] = useState(false);

  // ── Filtre state ──────────────────────────────────────────────────────────
  const [filterFaturaNo, setFilterFaturaNo] = useState('');          // belgeno2 -> belgeno
  const [filterCari,    setFilterCari]    = useState('');            // cariunvan
  const [filterFaturaTuru, setFilterFaturaTuru] = useState('');      // turuack / turu_kisa
  const [filterDurum,   setFilterDurum]   = useState<'' | '0' | '1' | '2'>(''); // şimdilik client-side, 2. aşama

  // ── Seçim state ───────────────────────────────────────────────────────────
  const [activeInvoice, setActiveInvoice]   = useState<IInvoiceListRow | null>(null);
  const [selectedInvoiceKey, setSelectedInvoiceKey] = useState<number | null>(null); // scf_fatura._key
  const [selectedKalemKeys, setSelectedKalemKeys] = useState<string[]>([]); // scf_fatura_kalemi.key
  const [selectedInvoiceKeys, setSelectedInvoiceKeys] = useState<number[]>([]); // toplu seçim
  const [selectedLineKeysByInvoice, setSelectedLineKeysByInvoice] = useState<Record<number, string[]>>({});

  const [activeTab, setActiveTab] = useState<'pool' | 'viewer' | 'last'>('pool');
  // İlk açılışta hızlı liste: "hepsi" (sınıflandırma taraması yok)
  const [transferTypeFilter, setTransferTypeFilter] = useState<TransferTypeFilter>('hepsi');
  const [ustIslemTuruFilter, setUstIslemTuruFilter] = useState<UstIslemTuruFilter>('');

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

  // ── Görüntülenecek Firma Faturaları (ayrı panel) ─────────────────────────
  const [viewerFirmaKodu, setViewerFirmaKodu] = useState<number | ''>('');
  const [viewerFirmaAdi, setViewerFirmaAdi] = useState<string>('');
  const [viewerPeriods, setViewerPeriods] = useState<ISourcePeriodDto[]>([]);
  const [viewerDonemKodu, setViewerDonemKodu] = useState<number | ''>('');
  /** Her “Görüntülenecek firma listele” işlemi yeni sekme ekler; sekme kendi fatura listesini tutar. */
  const [viewerTabs, setViewerTabs] = useState<ViewerTabState[]>([]);
  const [activeViewerTabId, setActiveViewerTabId] = useState<string | null>(null);
  const viewerPageSize = 100;
  const [viewerLastInvoice, setViewerLastInvoice] = useState<IInvoiceListRow | null>(null);
  const [viewerLastLookupLoading, setViewerLastLookupLoading] = useState(false);

  const activeViewerTab = useMemo(
    () => viewerTabs.find(t => t.id === activeViewerTabId) ?? null,
    [viewerTabs, activeViewerTabId]
  );
  const viewerInvoicesLoading = Boolean(activeViewerTab?.loading);
  const viewerRows = activeViewerTab?.invoices ?? [];
  const viewerHasMore = activeViewerTab?.hasMore ?? false;

  // ── Log state ─────────────────────────────────────────────────────────────
  const [logs,         setLogs]         = useState<ITransferLogEntry[]>([]);
  const [transferring, setTransferring] = useState(false);
  const [lookupError, setLookupError] = useState<string | null>(null);
  const [transferAlert, setTransferAlert] = useState<string | null>(null);
  const [autoListedOnce, setAutoListedOnce] = useState(false);
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
  }>>([]);

  const resetSelectionState = useCallback((clearInvoices = false) => {
    setActiveInvoice(null);
    setSelectedInvoiceKey(null);
    setSelectedKalemKeys([]);
    setSelectedInvoiceKeys([]);
    setSelectedLineKeysByInvoice({});
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
    // Son aktarım/son hedef fatura bilgisi de taşımasın.
    setLastTransfers([]);
    setViewerLastInvoice(null);
    // UX: filtre/şube/dönem değişiminde tabloyu "boş" yapma; loading overlay ile yenisini getir.
    // Sadece manuel refresh gibi durumlarda clearInvoices kullan.
    if (clearInvoices) setInvoices([]);
  }, []);

  const clearLastTransferState = useCallback(() => {
    setLastTransfers([]);
    setViewerLastInvoice(null);
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
        const bs = await retry(() => InvoiceService.getBranches(selectedPoolFirmaKodu));
        setBranches(bs);
        if (selectedSube > 0 && bs.some(x => x.key === selectedSube)) {
          setSourceSubeKey(selectedSube);
        } else {
          // Otomatik şube seçme: varsayılan "Tüm Şubeler"
          selectedSube = 0;
          setSourceSubeKey('');
        }
      } catch (err) {
        console.error('[Init] branches failed', err);
        setBranches([]);
      }

      try {
        // Depo şubeye bağlı; "Tüm Şubeler" seçiliyken depo disabled olmalı.
        if (!selectedPoolFirmaKodu || selectedSube <= 0) {
          setSourceDepots([]);
          setSourceDepoKey('');
        } else {
          const deps = await retry(() => InvoiceService.getDepots(selectedPoolFirmaKodu, selectedSube));
          setSourceDepots(deps);
          setSourceDepoKey('');
        }
      } catch (err) {
        console.error('[Init] depots failed', err);
        setSourceDepots([]);
        setSourceDepoKey('');
      }
    };
    init();
  }, []); // eslint-disable-line



  // ── Fatura listesi yükle ──────────────────────────────────────────────────
  const invoiceListReqId = useRef(0);
  const fetchInvoices = useCallback(async () => {
    const myReqId = ++invoiceListReqId.current;
    if (!poolFirmaKodu || !sourceDonemKodu) {
      setInvoices([]);
      setLoading(false);
      setTransferAlert(
        'Kaynak havuz veya dönem yüklenemedi. Backend (5189) açık mı kontrol edin; ardından Yenile’ye basın.'
      );
      return;
    }
    setLoading(true);
    try {
      setTransferAlert(null);
      const parts: string[] = [];
      if (filterFaturaNo.trim()) {
        const q = filterFaturaNo.trim().replaceAll("'", "''");
        parts.push(`([belgeno2] LIKE '%${q}%' OR [belgeno] LIKE '%${q}%')`);
      }
      if (filterCari.trim()) {
        const q = filterCari.trim().replaceAll("'", "''");
        // Tenant farkı: bazı listelerde alan adı __cariunvan gelebiliyor.
        parts.push(`([__cariunvan] LIKE '%${q}%' OR [cariunvan] LIKE '%${q}%')`);
      }
      const filters = parts.join(' AND ');

      // İş kuralı: Dağıtılacak = en az bir kalemde dinamik şube dolu; Tüm Faturalar = hiçbirinde dolu değil.
      const onlyDistributable = transferTypeFilter === 'dagitilacak_faturalar';
      const onlyNonDistributable = transferTypeFilter === 'tum_faturalar';
      const ustIslemTuruKey =
        ustIslemTuruFilter === 'A' ? 39715 :
        ustIslemTuruFilter === 'B' ? 39717 :
        undefined;
      // Not: Bu iki modda backend "filtered-scan" yapıyor (kalemlerden karar veriyor).
      // Sayfa sayfa çekmek O(N^2) maliyet yaratıyor. Bu yüzden tek istekle getiriyoruz.
      const pageSize = 200;
      const doSingleShot = onlyDistributable || onlyNonDistributable;
      const allRows: IInvoiceListRow[] = [];
      let offset = 0;
      while (true) {
        if (invoiceListReqId.current !== myReqId) return; // stale
        const payload = {
          firma_kodu: poolFirmaKodu,
          donem_kodu: sourceDonemKodu,
          // server-side de filtre ekliyor; burada da filters içinde gönderiyoruz
          source_sube_key: sourceSubeKey === '' ? undefined : Number(sourceSubeKey),
          source_depo_key: sourceDepoKey === '' ? undefined : Number(sourceDepoKey),
          filters: filters || undefined,
          ust_islem_turu_key: ustIslemTuruKey,
          // Aktarım türü filtresi backend'e yansır.
          only_distributable: onlyDistributable ? true : undefined,
          only_non_distributable: onlyNonDistributable ? true : undefined,
          limit: pageSize,
          offset,
        };
        const page = await InvoiceService.listInvoices(payload);
        if (invoiceListReqId.current !== myReqId) return; // stale
        allRows.push(...page);
        if (doSingleShot) break;
        if (page.length < pageSize) break;
        offset += page.length;
        if (offset >= 5000) break; // safety cap
      }

      if (invoiceListReqId.current !== myReqId) return;
      setInvoices(allRows);
      setActiveInvoice(prev => prev ? (allRows.find(i => i.key === prev.key) ?? null) : null);

      // Liste yenilendiyse (filtre/değişim/race) eski seçili kalemleri asla taşımayalım.
      setSelectedKalemKeys([]);
      setSelectedLineKeysByInvoice({});

    } catch (err: any) {
      if (invoiceListReqId.current !== myReqId) return;
      const msg = err?.response?.data?.message ?? err?.message ?? 'Fatura listesi yüklenemedi.';
      setTransferAlert(msg);
      pushLog({ source_kalem_key: '', status: 'error', message: `Fatura listesi yüklenemedi: ${msg}`, was_duplicate_override: false });
    }
    if (invoiceListReqId.current === myReqId) setLoading(false);
  }, [poolFirmaKodu, sourceDonemKodu, sourceSubeKey, sourceDepoKey, filterFaturaNo, filterCari, transferTypeFilter, ustIslemTuruFilter]);

  const fetchInvoiceDetailByKey = useCallback(async (
    invoiceKey: number,
    fisNo?: string,
    ctx?: { firmaKodu: number; donemKodu: number }
  ) => {
    setLinesLoading(true);
    try {
      const fk = ctx?.firmaKodu ?? poolFirmaKodu;
      const dk = ctx?.donemKodu ?? sourceDonemKodu;
      const res = await InvoiceService.getInvoice(invoiceKey, {
        firmaKodu: fk,
        donemKodu: dk,
      });
      const detail = res.data as any;
      const kalemler = detail?.kalemler ?? detail?.Kalemler ?? [];
      setLines(kalemler);
      // Transfer fallback için snapshot cache
      setLineSnapshotsByInvoice(prev => {
        if (!invoiceKey || !Number.isFinite(invoiceKey) || invoiceKey <= 0) return prev;
        const snaps = (kalemler ?? []).map((l: any) => ({
          sourceLineKey: (() => {
            const k = Number(normalizeLineKey(l.key));
            if (Number.isFinite(k) && k > 0) return k;
            const s = Number(l.sirano ?? l.SiraNo ?? 0);
            if (Number.isFinite(s) && s > 0) return s;
            return undefined;
          })(),
          stokKartKodu: (l.stokhizmetkodu ?? '').toString(),
          aciklama: (l.stokhizmetaciklama ?? '').toString(),
          miktar: typeof l.miktar === 'number' ? l.miktar : Number(l.miktar ?? 0),
          birimFiyati: typeof l.birimfiyati === 'number' ? l.birimfiyati : Number(l.birimfiyati ?? 0),
          tutar: typeof l.tutari === 'number' ? l.tutari : Number(l.tutari ?? 0),
        }));
        return { ...prev, [invoiceKey]: snaps };
      });
    } catch (err) {
      // Geçici backend/DİA dalgalanmasında kullanıcı seçimini "boşaltma";
      // mevcut satırlar ekranda kalsın, sadece hata loglansın.
      pushLog({
        source_kalem_key: '',
        status: 'error',
        message: `Kalemler yüklenemedi (detail): ${fisNo ?? invoiceKey}`,
        was_duplicate_override: false,
      });
    } finally {
      setLinesLoading(false);
    }
  }, [poolFirmaKodu, sourceDonemKodu]);

  const fetchViewerInvoices = useCallback(async (mode: 'newTab' | 'appendActive' = 'newTab') => {
    const fk =
      mode === 'appendActive'
        ? Number(activeViewerTab?.firmaKodu ?? viewerFirmaKodu)
        : Number(viewerFirmaKodu);
    const dk =
      mode === 'appendActive'
        ? Number(activeViewerTab?.donemKodu ?? viewerDonemKodu)
        : Number(viewerDonemKodu);
    if (!fk || !dk) return;

    const newId = crypto.randomUUID();
    let tabId: string | null = mode === 'newTab' ? newId : activeViewerTabId;
    if (mode === 'appendActive' && (!tabId || !activeViewerTab)) return;

    const setTabLoading = (id: string | null, loading: boolean) => {
      if (!id) return;
      setViewerTabs(prev => prev.map(t => (t.id === id ? { ...t, loading } : t)));
    };

    const currentOffset = mode === 'appendActive' ? (activeViewerTab?.offset ?? 0) : 0;

    if (mode === 'newTab') {
      tabId = newId;
      setViewerTabs(prev => [
        ...prev,
        {
          id: tabId!,
          firmaKodu: fk,
          firmaAdi: viewerFirmaAdi || `Firma ${fk}`,
          donemKodu: dk,
          invoices: [],
          loading: true,
          offset: 0,
          hasMore: true,
        },
      ]);
      setActiveViewerTabId(tabId!);
    } else {
      setTabLoading(tabId, true);
    }

    if (!tabId) return;

    try {
      const parts: string[] = [];
      if (filterFaturaNo.trim()) {
        const q = filterFaturaNo.trim().replaceAll("'", "''");
        parts.push(`([belgeno2] LIKE '%${q}%' OR [belgeno] LIKE '%${q}%')`);
      }
      if (filterCari.trim()) {
        const q = filterCari.trim().replaceAll("'", "''");
        parts.push(`([__cariunvan] LIKE '%${q}%' OR [cariunvan] LIKE '%${q}%')`);
      }
      const filters = parts.join(' AND ');
      const payload = {
        firma_kodu: fk,
        donem_kodu: dk,
        filters: filters || undefined,
        limit: viewerPageSize,
        offset: currentOffset,
      };
      const data = await InvoiceService.listInvoices(payload);

      const hasMore = data.length >= viewerPageSize;
      const nextOffset = currentOffset + data.length;

      setViewerTabs(prev =>
        prev.map(t => {
          if (t.id !== tabId) return t;
          return {
            ...t,
            loading: false,
            invoices: mode === 'newTab' ? data : [...t.invoices, ...data],
            offset: nextOffset,
            hasMore,
          };
        })
      );
    } catch {
      pushLog({ source_kalem_key: '', status: 'error', message: 'Görüntülenecek firma faturaları yüklenemedi.', was_duplicate_override: false });
      if (mode === 'newTab') {
        setViewerTabs(prev => prev.filter(t => t.id !== tabId));
        setActiveViewerTabId(prevActive => (prevActive === tabId ? null : prevActive));
      } else {
        setTabLoading(tabId, false);
      }
    }
  }, [viewerFirmaKodu, viewerDonemKodu, viewerFirmaAdi, viewerPageSize, activeViewerTabId, activeViewerTab, filterFaturaNo, filterCari]);

  const fetchLastTransferredInvoice = useCallback(async (createdInvoiceKey: number, firmaKodu: number, donemKodu: number) => {
    if (!createdInvoiceKey || !firmaKodu || !donemKodu) return;
    setViewerLastLookupLoading(true);
    try {
      const payload = {
        firma_kodu: Number(firmaKodu),
        donem_kodu: Number(donemKodu),
        filters: `[_key] = ${Number(createdInvoiceKey)}`,
        limit: 1,
        offset: 0,
      };
      const data = await InvoiceService.listInvoices(payload);
      setViewerLastInvoice(data[0] ?? null);
    } catch {
      setViewerLastInvoice(null);
      pushLog({ source_kalem_key: '', status: 'error', message: 'Son aktarılan hedef kayıt getirilemedi.', was_duplicate_override: false });
    } finally {
      setViewerLastLookupLoading(false);
    }
  }, []);

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

  // ── Fatura seçilince kalemleri yükle ─────────────────────────────────────
  const openInvoice = async (inv: IInvoiceListRow) => {
    // Kullanıcı başka faturaya geçtiğinde hedef aktarım paneli her zaman sıfırlansın.
    resetTargetTransferContext();

    setActiveInvoice(inv);
    const key = Number(inv.key);
    setSelectedInvoiceKey(Number.isFinite(key) ? key : null);
    // Kritik: yeni faturaya geçince eski seçimler taşınmasın.
    setSelectedKalemKeys([]);
    if (Number.isFinite(key) && key > 0) {
      setSelectedLineKeysByInvoice(m => ({ ...m, [key]: [] }));
    }
    setLines([]);
    if (!Number.isFinite(key)) {
      pushLog({ source_kalem_key: '', status: 'error', message: `Kalemler yüklenemedi: geçersiz invoice key (${inv.key})`, was_duplicate_override: false });
      return;
    }
    await fetchInvoiceDetailByKey(key, inv.fisno);
  };

  useEffect(() => {
    if (!poolFirmaKodu || !sourceSubeKey) {
      setSourceDepots([]);
      setSourceDepoKey('');
      return;
    }
    InvoiceService.getDepots(poolFirmaKodu, Number(sourceSubeKey))
      .then(deps => {
        setSourceDepots(deps);
        // Kullanıcı depo seçimini bozma: geçerli değilse boş bırak (auto-select yapma)
        setSourceDepoKey(prev => (prev !== '' && deps.some(d => d.key === prev)) ? prev : '');
      })
      .catch(() => {
        setSourceDepots([]);
      });
  }, [poolFirmaKodu, sourceSubeKey]);

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

  // Şube lookup bazen ilk açılışta boş kalabiliyor; şube boşsa sessizce toparla.
  useEffect(() => {
    if (!poolFirmaKodu) return;
    if (branches.length > 0) return;
    InvoiceService.getBranches(poolFirmaKodu)
      .then(bs => setBranches(prev => (bs.length > 0 ? bs : prev)))
      .catch(() => {
        // ignore (mevcut state'i ezme)
      });
  }, [poolFirmaKodu, branches.length]);

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

  // Şube/depo/dönem değişince listeyi kullanıcı beklediği gibi otomatik güncelle.
  useEffect(() => {
    if (!autoListedOnce) return;
    if (!poolFirmaKodu || !sourceDonemKodu) return;
    void fetchInvoices();
  }, [poolFirmaKodu, sourceDonemKodu, sourceSubeKey, sourceDepoKey, autoListedOnce, fetchInvoices]);

  useEffect(() => {
    if (!poolFirmaKodu || !sourceDonemKodu) return;
    if (autoListedOnce) return;
    fetchInvoices().finally(() => setAutoListedOnce(true));
  }, [poolFirmaKodu, sourceDonemKodu, autoListedOnce, fetchInvoices]);

  useEffect(() => {
    resetSelectionState(false);
  }, [filterFaturaNo, filterCari, filterFaturaTuru, transferTypeFilter, resetSelectionState]);

  const resolveTargetForFirma = useCallback(async (fk: number) => {
    if (!fk || !Number.isFinite(fk) || fk <= 0) return;
    // Kaynak fatura tarihi yoksa resolve-target çağırma (DİA 400 / dönem bulunamadı spamını önler).
    const srcDate = activeInvoice?.tarih;
    if (!srcDate) return;
    try {
      const found = companies.find(c => c.firma_kodu === fk);
      setTargetFirmaAdi(found?.firma_adi ?? '');
      const res = await retry(() => InvoiceService.resolveTarget({
        targetFirmaKodu: fk,
        sourceDonemKodu: Number(sourceDonemKodu) || undefined,
        sourceInvoiceDate: srcDate,
      }), 2);

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

  // ── Hedef firma değişince backend auto-resolve ─────────────────────────────
  const handleTargetFirmaChange = useCallback((firmaCodeStr: string) => {
    const fk = parseInt(firmaCodeStr);
    clearLastTransferState();
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
  }, [clearLastTransferState, companies]);

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

  const handleViewerFirmaChange = useCallback(async (firmaCodeStr: string) => {
    if (!firmaCodeStr || firmaCodeStr === '__none__') {
      setViewerFirmaKodu('');
      setViewerFirmaAdi('');
      setViewerPeriods([]);
      setViewerDonemKodu('');
      setViewerTabs([]);
      setActiveViewerTabId(null);
      setViewerLastInvoice(null);
      return;
    }
    const fk = parseInt(firmaCodeStr);
    setViewerFirmaKodu(fk || '');
    const found = companies.find(c => c.firma_kodu === fk);
    setViewerFirmaAdi(found?.firma_adi ?? '');
    setViewerPeriods([]);
    setViewerDonemKodu('');
    setViewerTabs([]);
    setActiveViewerTabId(null);
    setViewerLastInvoice(null);
    if (!fk) return;
    try {
      const ps = await retry(() => InvoiceService.getPeriods(fk));
      setViewerPeriods(ps);
      const ont = ps.find(p => p.ontanimli) ?? ps[0];
      if (ont) setViewerDonemKodu(ont.donemkodu);
    } catch {
      setViewerPeriods([]);
      setViewerDonemKodu('');
    }
  }, [companies]);

  const pushLog = (log: ITransferLogEntry) =>
    setLogs(prev => [log, ...prev].slice(0, 100));

  const applyViewerTabToChrome = useCallback((t: ViewerTabState) => {
    setViewerFirmaKodu(t.firmaKodu);
    setViewerFirmaAdi(t.firmaAdi);
    setViewerDonemKodu(t.donemKodu);
    void InvoiceService.getPeriods(t.firmaKodu).then(setViewerPeriods).catch(() => {});
  }, []);

  const selectViewerTab = useCallback(
    (t: ViewerTabState) => {
      setActiveViewerTabId(t.id);
      applyViewerTabToChrome(t);
    },
    [applyViewerTabToChrome]
  );

  const closeViewerTab = useCallback(
    (id: string, e: MouseEvent<HTMLButtonElement>) => {
      e.stopPropagation();
      setViewerTabs(prev => {
        const next = prev.filter(t => t.id !== id);
        setActiveViewerTabId(cur => {
          if (cur !== id) return cur;
          const fb = next[next.length - 1];
          if (fb) queueMicrotask(() => applyViewerTabToChrome(fb));
          return fb?.id ?? null;
        });
        return next;
      });
    },
    [applyViewerTabToChrome]
  );

  // ── Client-side filtre (transfer_status — uygulama özel) ─────────────────
  const filteredInvoices = useMemo(() => {
    const norm = (s: string) =>
      (s ?? '')
        .toString()
        .trim()
        .toLocaleLowerCase('tr-TR')
        .replaceAll('ı', 'i')
        .replaceAll('İ', 'i')
        .replaceAll('ş', 's')
        .replaceAll('ğ', 'g')
        .replaceAll('ü', 'u')
        .replaceAll('ö', 'o')
        .replaceAll('ç', 'c')
        .replace(/\s+/g, ' ');

    let list = invoices;
    if (filterFaturaNo.trim()) {
      const q = norm(filterFaturaNo);
      list = list.filter(i => norm((i.belgeno2 || i.belgeno || '')).includes(q));
    }
    if (filterCari.trim()) {
      const q = norm(filterCari);
      list = list.filter(i => norm(i.cariunvan ?? '').includes(q));
    }
    if (filterFaturaTuru) {
      list = list.filter(i => {
        const v = (i.turuack || i.turu_kisa || (i as any).turukisa || '').trim();
        return v === filterFaturaTuru;
      });
    }
    if (!filterDurum) return list;
    const statusMap: Record<string, TransferStatus> = {
      '0': 'Bekliyor', '1': 'Kismi', '2': 'Aktarildi',
    };
    return list.filter(inv => inv.transfer_status === statusMap[filterDurum]);
  }, [invoices, filterDurum, filterFaturaNo, filterCari, filterFaturaTuru]);

  const [invoiceTypeOptions, setInvoiceTypeOptions] = useState<string[]>([]);
  const [invoiceTypesLoading, setInvoiceTypesLoading] = useState(false);

  useEffect(() => {
    const run = async () => {
      if (!poolFirmaKodu || !sourceDonemKodu) return;
      setInvoiceTypesLoading(true);
      try {
        const types = await InvoiceService.getInvoiceTypes({
          firmaKodu: poolFirmaKodu,
          donemKodu: sourceDonemKodu,
          sourceSubeKey: sourceSubeKey,
          sourceDepoKey: sourceDepoKey,
        });
        setInvoiceTypeOptions(types);
      } catch {
        // Fallback: mevcut `invoices` state’inden üret.
        const set = new Set<string>();
        for (const i of invoices) {
          const v = (i.turuack ?? '').trim();
          if (v) set.add(v);
        }
        setInvoiceTypeOptions(Array.from(set).sort((a, b) => a.localeCompare(b, 'tr')));
      } finally {
        setInvoiceTypesLoading(false);
      }
    };

    run();
  }, [poolFirmaKodu, sourceDonemKodu, sourceSubeKey, sourceDepoKey, invoices]); // eslint-disable-line

  const normalizeLineKey = (k: string | number | null | undefined) => String(k ?? '');
  const normalizeInvoiceKey = (k: string | number | null | undefined) => {
    const n = Number(k);
    return Number.isFinite(n) ? n : 0;
  };

  const toggleInvoice = (rawKey: string | number | null | undefined) => {
    const k = normalizeInvoiceKey(rawKey);
    if (!k) return;
    setSelectedInvoiceKeys(prev => (prev.includes(k) ? prev.filter(x => x !== k) : [...prev, k]));
  };

  const toggleAllVisibleInvoices = () => {
    const keys = filteredInvoices
      .map(i => normalizeInvoiceKey(i.key))
      .filter(k => Number.isFinite(k) && k > 0);
    if (keys.length === 0) return;
    setSelectedInvoiceKeys(prev => {
      const allSelected = keys.every(k => prev.includes(k));
      return allSelected ? prev.filter(k => !keys.includes(k)) : Array.from(new Set([...prev, ...keys]));
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


  const selectedTotal = useMemo(
    () => selectedLines.reduce((s, l) => s + (l.tutari ?? 0), 0),
    [selectedLines]
  );

  const selectedKdvTotal = useMemo(
    () => selectedLines.reduce((s, l) => s + (l.kdvtutari ?? 0), 0),
    [selectedLines]
  );

  // ── Duplicate risk (uygulama özel hesaplama) ──────────────────────────────
  const duplicateRiskCount = 0;


  // ── Transfer butonu kontrol ───────────────────────────────────────────────
  // Not: Bu sürümde gerçek aktarım yok; ama UI enable koşulunu doğru hesaplıyoruz.
  const canListSource = Boolean(poolFirmaKodu) && Boolean(sourceDonemKodu);
  const effectiveInvoiceKeys = useMemo(() => {
    return selectedInvoiceKeys.length > 0
      ? selectedInvoiceKeys
      : (selectedInvoiceKey ? [selectedInvoiceKey] : []);
  }, [selectedInvoiceKeys, selectedInvoiceKey]);

  // Not: Toplu aktarımda kalem seçimi opsiyonel. Kalem seçilmezse backend tüm kalemleri aktarır.

  const canTransfer =
    canListSource &&
    !(sourceSubeKey === '' && sourceDepoKey !== '') &&
    // otomatik seçim yok: kullanıcı checkbox ile fatura(lar) seçmeli
    effectiveInvoiceKeys.length > 0 &&
    Boolean(targetFirmaKodu) &&
    Boolean(targetDonemKodu) &&
    Boolean(targetSubeKey) &&
    Boolean(targetDepoKey) &&
    !Boolean(autoTargetLockError) &&
    !targetSelectionRuleError;

  // ── Kalem seçim işlemleri ─────────────────────────────────────────────────
  const toggleLine = (rawKey: string | number | null | undefined) => {
    const key = normalizeLineKey(rawKey);
    if (!key) return;
    setSelectedKalemKeys(prev => {
      const next = prev.includes(key) ? prev.filter(k => k !== key) : [...prev, key];
      if (selectedInvoiceKey) {
        setSelectedLineKeysByInvoice(m => ({ ...m, [selectedInvoiceKey]: next }));
      }
      return next;
    });
  };

  const toggleAllLines = () => {
    const free = lines
      .filter(l => l.transfer_status !== 'Aktarildi')
      .map(l => normalizeLineKey(l.key))
      .filter(Boolean);
    const allSel = free.length > 0 && free.every(k => selectedKalemKeys.includes(k));
    const next = allSel ? [] : free;
    setSelectedKalemKeys(next);
    if (selectedInvoiceKey) {
      setSelectedLineKeysByInvoice(m => ({ ...m, [selectedInvoiceKey]: next }));
    }
  };

  useEffect(() => {
    const validKeys = new Set(lines.map(l => normalizeLineKey(l.key)).filter(Boolean));
    setSelectedKalemKeys(prev => prev.filter(k => validKeys.has(k)));
  }, [lines]);

  const transferBlockers = useMemo(() => {
    const blockers: string[] = [];
    if (effectiveInvoiceKeys.length === 0) blockers.push('Kayıt seçilmedi.');
    if (autoTargetLockError) blockers.push(autoTargetLockError);
    if (targetSelectionRuleError) blockers.push(targetSelectionRuleError);
    if (!targetFirmaKodu) blockers.push('Hedef firma seçin');
    if (targetFirmaKodu && !targetSubeKey) blockers.push('Hedef şube otomatik bulunamadı');
    if (targetFirmaKodu && !targetDepoKey) blockers.push('Hedef depo otomatik bulunamadı');
    if (targetFirmaKodu && !targetDonemKodu) blockers.push('Kaynak döneme karşılık hedef firmada uygun dönem yok.');
    return blockers;
  }, [effectiveInvoiceKeys.length, autoTargetLockError, targetSelectionRuleError, targetFirmaKodu, targetSubeKey, targetDepoKey, targetDonemKodu]);

  const isBulkTransfer = selectedInvoiceKeys.length > 0;
  const willAutoSelectAllLines = (selectedKalemKeys.length === 0) && (effectiveInvoiceKeys.length > 0);
  const lastTransfer = lastTransfers.length > 0 ? lastTransfers[lastTransfers.length - 1] : null;

  // ── Aktarım ───────────────────────────────────────────────────────────────
  const handleTransfer = async () => {
    if (!canTransfer) return;
    setTransferring(true);
    setTransferAlert(null);
    clearLastTransferState();
    try {
      for (const invKey of effectiveInvoiceKeys) {
        const lineKeys = selectedLineKeysByInvoice[invKey] ?? (invKey === selectedInvoiceKey ? selectedKalemKeys : []);
        // Kalem seçilmemişse backend otomatik tüm kalemleri seçecek.
        // (Toplu seçimle kalem seçmeden aktarım için.)
        let selectedIds: number[] = [];
        let snapshots: any[] = [];
        if (lineKeys && lineKeys.length > 0) {
          const chosenLines = lines.filter(l => lineKeys.includes(normalizeLineKey(l.key)));
          selectedIds = chosenLines
            .map(l => {
              const k = Number(normalizeLineKey(l.key));
              if (Number.isFinite(k) && k > 0) return k;
              const s = Number(l.sirano ?? 0);
              if (Number.isFinite(s) && s > 0) return s;
              return null;
            })
            .filter((x): x is number => x !== null);

          snapshots = (lineSnapshotsByInvoice[invKey] ?? [])
            .filter((s: any) => selectedIds.includes(Number(s.sourceLineKey)))
            .map((s: any) => ({
              sourceLineKey: s.sourceLineKey,
              stokKartKodu: s.stokKartKodu,
              aciklama: s.aciklama,
              miktar: s.miktar,
              birimFiyati: s.birimFiyati,
              tutar: s.tutar,
            }));
        } else {
          // Bilgi mesajı: kullanıcı istemiyor (işlem günlüğünü şişiriyor)
        }

        const payload = {
          sourceFirmaKodu: poolFirmaKodu,
          sourceDonemKodu: sourceDonemKodu,
          sourceSubeKey: sourceSubeKey === '' ? undefined : Number(sourceSubeKey),
          sourceDepoKey: sourceDepoKey === '' ? undefined : Number(sourceDepoKey),
          sourceInvoiceKey: invKey,
          selectedKalemKeys: selectedIds,
          selectedLineSnapshots: snapshots,
          targetFirmaKodu: Number(targetFirmaKodu),
          targetDonemKodu: Number(targetDonemKodu) || 0,
          targetSubeKey: Number(targetSubeKey) || 0,
          targetDepoKey: Number(targetDepoKey) || 0,
        };

        let res: any;
        try {
          res = await InvoiceService.transferInvoice(payload);
        } catch (err: any) {
          res = err?.response?.data;
        }

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
          setTransferAlert(failMessageFull);
          pushLog({ source_kalem_key: '', status: 'error', message: `Aktarım başarısız: invoiceKey=${invKey} | ${failMessageFull}`, was_duplicate_override: false });
          continue;
        }

        const verified = Boolean(res?.createdVerified);
        setLastTransfers(prev => ([
          ...prev,
          {
            createdInvoiceKey: Number(res.createdInvoiceKey),
            targetFirmaKodu: Number(targetFirmaKodu),
            targetFirmaAdi: targetFirmaAdi,
            targetDonemKodu: Number(targetDonemKodu),
            targetSubeKey: Number(targetSubeKey),
            targetDepoKey: Number(targetDepoKey),
          }
        ]));

        pushLog({
          source_kalem_key: '',
          status: verified ? 'success' : 'error',
          message: `${res.message ?? 'Aktarım'} (key=${res.createdInvoiceKey}) | ${verified ? 'doğrulandı' : 'doğrulanamadı'}`,
          was_duplicate_override: false,
        });
        if (!verified) {
          setTransferAlert(res?.message ?? 'Kayıt oluşturuldu ancak doğrulama başarısız.');
        }

        if (verified && targetFirmaKodu && targetDonemKodu) {
          setViewerFirmaKodu(Number(targetFirmaKodu));
          setViewerFirmaAdi(targetFirmaAdi);
          setViewerPeriods(targetPeriods);
          setViewerDonemKodu(Number(targetDonemKodu));
          await fetchLastTransferredInvoice(Number(res.createdInvoiceKey), Number(targetFirmaKodu), Number(targetDonemKodu));
          setActiveTab('last');
        }

        if ((res.duplicateSkippedCount ?? 0) > 0) {
          pushLog({
            source_kalem_key: '',
            status: 'duplicate',
            message: `${res.duplicateSkippedCount} kalem duplicate nedeniyle atlandı.`,
            was_duplicate_override: false,
          });
        }
      }

      // UI refresh
      await fetchInvoices();
    } catch (err: any) {
      clearLastTransferState();
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


  const resetFilters = () => {
    setFilterFaturaNo('');
    setFilterCari('');
    setFilterFaturaTuru('');
    setFilterDurum('');
    setTransferTypeFilter('hepsi');
    resetSelectionState(true);
  };

  const handleRefresh = () => {
    setLoading(true);
    setLinesLoading(false);
    setLookupError(null);
    setTransferAlert(null);
    resetSelectionState(true);
    clearLastTransferState();
    setFilterFaturaNo('');
    setFilterCari('');
    setFilterFaturaTuru('');
    setFilterDurum('');
    setTransferTypeFilter('hepsi');
    setAutoListedOnce(false);
    setLogs([]);
    setTimeout(() => window.location.reload(), 80);
  };

  const handleClearTransferState = async () => {
    if (!selectedInvoiceKey) return;
    try {
      const res = await InvoiceService.clearTransferState({ sourceInvoiceKey: Number(selectedInvoiceKey) });
      pushLog({ source_kalem_key: '', status: 'success', message: `Aktarım durumu sıfırlandı. invoiceKey=${selectedInvoiceKey} cleared=${res.cleared}`, was_duplicate_override: false });
      await fetchInvoices();
      if (selectedInvoiceKey) await fetchInvoiceDetailByKey(selectedInvoiceKey);
    } catch (e: any) {
      pushLog({ source_kalem_key: '', status: 'error', message: `Aktarım durumu sıfırlanamadı: ${e?.message ?? 'hata'}`, was_duplicate_override: false });
    }
  };


  // ── Badge & yardımcılar ───────────────────────────────────────────────────
  const transferBadge = (s: any) => {
    // Hem string hem sayı (Enum) uyumluluğu için esnek kontrol
    if (s === 'Aktarildi' || s === 2) return <span className="erp-badge erp-badge-ok">Aktarıldı</span>;
    if (s === 'Kismi'     || s === 1) return <span className="erp-badge erp-badge-warn">Kısmi</span>;
    if (s === 'Hata'      || s === 3) return <span className="erp-badge erp-badge-err">Hata</span>;
    return <span className="erp-badge erp-badge-idle">Bekliyor</span>;
  };

  const logCls  = (s: string) => s === 'success' ? 'erp-log-success' : s === 'duplicate' ? 'erp-log-warn' : 'erp-log-error';
  const logIcon = (s: string) => s === 'success' ? '✔' : s === 'duplicate' ? '⚠' : '✖';

  // ─────────────────────────────────────────────────────────────────────────
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
            disabled={!selectedInvoiceKey}
            title="DIA’da hedef kaydı sildiyseniz, burada sıfırlayın."
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

          {/* Kaynak Dönem */}
          <div className="erp-filter-group">
            <label>Kaynak Dönem</label>
            <select
              value={sourceDonemKodu}
              onChange={e => setSourceDonemKodu(parseInt(e.target.value))}
              disabled={periods.length === 0 && !sourceDonemKodu}
            >
              {periods.length === 0 && sourceDonemKodu > 0 && (
                <option value={sourceDonemKodu}>{sourceDonemKodu} (Varsayılan)</option>
              )}
              {(() => {
                const yearCounts: Record<string, number> = {};
                for (const p of periods) {
                  const b = Date.parse(p.baslangic_tarihi || '');
                  const e = Date.parse(p.bitis_tarihi || '');
                  const y =
                    (!Number.isNaN(b) && !Number.isNaN(e))
                      ? (new Date(b).getFullYear() === new Date(e).getFullYear()
                        ? String(new Date(b).getFullYear())
                        : `${new Date(b).getFullYear()}–${new Date(e).getFullYear()}`)
                      : (p.gorunenkod || String(p.donemkodu));
                  yearCounts[y] = (yearCounts[y] ?? 0) + 1;
                }

                return periods.map(p => (
                <option key={p.key} value={p.donemkodu}>
                  {(() => {
                    const b = Date.parse(p.baslangic_tarihi || '');
                    const e = Date.parse(p.bitis_tarihi || '');
                    if (!Number.isNaN(b) && !Number.isNaN(e)) {
                      const by = new Date(b).getFullYear();
                      const ey = new Date(e).getFullYear();
                      const y = by === ey ? `${by}` : `${by}–${ey}`;
                      const suffix = (yearCounts[y] ?? 0) > 1 ? ` (D${p.donemkodu})` : '';
                      return `${y}${suffix}${p.ontanimli ? ' (Önt.)' : ''}`;
                    }
                    return `${p.gorunenkod || p.donemkodu}${p.ontanimli ? ' (Önt.)' : ''}`;
                  })()}
                </option>
                ));
              })()}
            </select>
          </div>

          {/* Kaynak Şube */}
          <div className="erp-filter-group">
            <label>Kaynak Şube</label>
            <select
              value={sourceSubeKey}
              onChange={e => {
                const v = e.target.value ? parseInt(e.target.value) : '';
                setSourceSubeKey(v);
                if (v === '') {
                  setSourceDepots([]);
                  setSourceDepoKey('');
                }
              }}
              disabled={false}
            >
              <option value="">Tüm Şubeler</option>
                {branches.length === 0 && (
                  <option value="" disabled>Şubeler yükleniyor...</option>
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
                const v = e.target.value ? parseInt(e.target.value) : '';
                setSourceDepoKey(v);
              }}
              disabled={sourceSubeKey === '' || (sourceDepots.length === 0)}
            >
              <option value="">Tüm Depolar</option>
                {sourceSubeKey !== '' && sourceDepots.length === 0 && (
                  <option value="" disabled>Depolar yükleniyor...</option>
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
                <option key={t} value={t}>{t}</option>
              ))}
            </select>
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

          <div className="erp-filter-group">
            <label>Görüntülenecek Firma</label>
            <select
              value={viewerFirmaKodu}
              onChange={e => handleViewerFirmaChange(e.target.value)}
              onFocus={() => { ensureCompaniesLoaded(true); }}
              style={{ width: 190 }}
            >
              <option value="__none__">Seçiniz</option>
              {companies
                .filter(c => c.firma_kodu !== poolFirmaKodu)
                .map(c => (
                  <option key={c.firma_kodu} value={c.firma_kodu}>
                    {c.firma_adi}
                  </option>
                ))}
            </select>
          </div>

          <div className="erp-filter-group">
            <label>Görüntülenecek Dönem</label>
            <select
              value={viewerDonemKodu}
              onChange={e => {
                const v = e.target.value ? parseInt(e.target.value) : '';
                setViewerDonemKodu(v);
                setViewerTabs([]);
                setActiveViewerTabId(null);
              }}
              disabled={!viewerFirmaKodu || viewerPeriods.length === 0}
              style={{ width: 150 }}
            >
              <option value="">Seçiniz</option>
              {viewerPeriods.map(p => (
                <option key={p.key} value={p.donemkodu}>
                  {p.gorunenkod}{p.ontanimli ? ' (Önt.)' : ''}
                </option>
              ))}
            </select>
            {viewerFirmaKodu && viewerPeriods.length === 0 && (
              <div className="erp-muted" style={{ fontSize: 11 }}>
                Bu firmada yetkili dönem bulunamadı.
              </div>
            )}
          </div>

          <div className="erp-filter-actions">
            <div className="erp-filter-group erp-filter-group-compact">
              <label>Aktarım Türü</label>
              <select
                value={transferTypeFilter}
                onChange={e => setTransferTypeFilter(e.target.value as TransferTypeFilter)}
                style={{ width: 190 }}
              >
                <option value="hepsi">Hepsi (Hızlı)</option>
                <option value="tum_faturalar">Tüm Faturalar</option>
                <option value="dagitilacak_faturalar">Dağıtılacak Faturalar</option>
              </select>
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
              onClick={() => fetchInvoices()}
              disabled={loading || !canListSource}
            >
              Listele
            </button>
            <button
              className="erp-btn erp-btn-primary"
              onClick={async () => {
                await fetchViewerInvoices('newTab');
                setActiveTab('viewer');
              }}
              disabled={!viewerFirmaKodu || !viewerDonemKodu || viewerTabs.some(t => t.loading)}
            >
              {viewerTabs.some(t => t.loading) ? '⟳ Yükleniyor...' : 'Görüntülenecek Firma Faturalarını Listele'}
            </button>
            <button
              className="erp-btn erp-btn-secondary"
              onClick={async () => {
                const last = lastTransfers[lastTransfers.length - 1];
                if (!last) return;
                setViewerFirmaKodu(last.targetFirmaKodu);
                setViewerFirmaAdi(last.targetFirmaAdi);
                setViewerDonemKodu(last.targetDonemKodu);
                await fetchLastTransferredInvoice(
                  last.createdInvoiceKey,
                  last.targetFirmaKodu,
                  last.targetDonemKodu
                );
                setActiveTab('last');
              }}
              disabled={lastTransfers.length === 0 || viewerLastLookupLoading}
            >
              Son Aktarılan Faturayı Göster
            </button>
            <button className="erp-btn erp-btn-secondary" onClick={resetFilters}>
              Temizle
            </button>
          </div>
        </div>
        <div className="erp-muted" style={{ fontSize: 11, marginTop: 2, color: '#334155' }}>
          Aktarım Türü: <strong>Tüm Faturalar</strong> yalnızca kalemlerinde şube (dinamik) seçimi olmayan faturaları listeler. <strong>Dağıtılacak Faturalar</strong> en az bir kalemde dinamik şube dolu olanları gösterir; aynı fatura iki listede yer almaz.
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
            <button className={`erp-btn ${activeTab === 'viewer' ? 'erp-btn-primary' : 'erp-btn-secondary'}`} onClick={() => setActiveTab('viewer')}>
              Hedef / Görüntülenen Firma Kayıtları
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
                  <span className="erp-src-tag">scf_fatura_liste_view</span>
                </span>
                <span className="erp-section-count">{filteredInvoices.length} kayıt</span>
              </div>
              <div className="erp-table-wrap">
                <table className="erp-table">
                  <thead>
                    <tr>
                      <th style={{ width: 32 }} className="text-center">
                        <input
                          type="checkbox"
                          className="erp-cb"
                          title="Görünen tüm faturaları seç/kaldır"
                          checked={
                            filteredInvoices.length > 0 &&
                            filteredInvoices
                              .map(i => normalizeInvoiceKey(i.key))
                              .filter(k => k > 0)
                              .every(k => selectedInvoiceKeys.includes(k))
                          }
                          onChange={toggleAllVisibleInvoices}
                        />
                      </th>
                      <th style={{ width: 32 }}></th>
                      {/* Standart scf_fatura_liste_view kolonları */}
                      <th>FATURA NO</th>
                      <th>FİŞ NO</th>
                      <th>TARİH</th>
                      <th>TÜR</th>
                      <th>CARİ KOD</th>
                      <th>CARİ ÜNVAN</th>
                      <th>KAYNAK ŞUBE</th>
                      <th>KAYNAK DEPO</th>
                      <th>DÖVİZ</th>
                      <th className="text-right">TOPLAM</th>
                      <th className="text-right">KDV</th>
                      <th className="text-right">NET</th>
                      {/* Uygulama özel kolonlar */}
                      <th className="text-center erp-th-custom">AKT. DURUM ★</th>
                    </tr>
                  </thead>
                  <tbody>
                    {filteredInvoices.length === 0 && (
                      <tr><td colSpan={15} className="erp-empty">
                        {loading
                          ? (isDistributableMode
                              ? 'Dağıtılacak/Virman faturalar taranıyor...'
                              : 'Yükleniyor...')
                          : (isDistributableMode
                              ? 'Seçili aktarım türüne göre kayıt bulunamadı.'
                              : 'Kayıt bulunamadı.')}
                      </td></tr>
                    )}
                    {filteredInvoices.map(inv => {
                      const isActive = activeInvoice?.key === inv.key;
                      const invKeyNum = normalizeInvoiceKey(inv.key);
                      const isSelected = invKeyNum > 0 && selectedInvoiceKeys.includes(invKeyNum);
                      return (
                        <tr
                          key={inv.key || inv.fisno}
                          className={isActive ? 'erp-row-active' : 'erp-row'}

                          onClick={() => openInvoice(inv)}
                          title="Kalemleri görüntüle"
                        >
                          <td className="text-center">
                            <input
                              type="checkbox"
                              className="erp-cb"
                              checked={isSelected}
                              disabled={!invKeyNum}
                              onChange={() => toggleInvoice(inv.key)}
                              onClick={e => e.stopPropagation()}
                              title="Toplu seçim"
                            />
                          </td>
                          <td className="text-center">
                            <input type="radio" readOnly checked={isActive} className="erp-radio" />
                          </td>
                          {/* Fatura No: belgeno2 -> belgeno */}
                          <td className="erp-fisno">{inv.belgeno2 || inv.belgeno || '—'}</td>
                          {/* Fiş No (artık arama yok) */}
                          <td className="erp-muted">{inv.fisno ?? '—'}</td>
                          {/* tarih */}
                          <td className="erp-date">{inv.tarih ? new Date(inv.tarih).toLocaleDateString('tr-TR') : '—'}</td>
                          {/* turuack */}
                          <td className="erp-muted erp-cell-std">{inv.turuack ?? '—'}</td>
                          {/* carikartkodu */}
                          <td className="erp-mono">{inv.carikartkodu}</td>
                          {/* cariunvan */}
                          <td className="erp-cari">{inv.cariunvan}</td>
                          {/* sourcesubeadi */}
                          <td><span className="erp-sube-tag">{inv.sourcesubeadi}</span></td>
                          {/* sourcedepoadi */}
                          <td><span className="erp-sube-tag">{inv.sourcedepoadi ?? '—'}</span></td>
                          {/* dovizturu */}
                          <td className="erp-dvz">{inv.dovizturu ?? '—'}</td>
                          {/* toplam */}
                          <td className="text-right erp-amount">{fmtShort(inv.toplam ?? 0)}</td>
                          {/* toplamkdv */}
                          <td className="text-right erp-amount erp-muted">{fmtShort(inv.toplamkdv ?? 0)}</td>
                          {/* net */}
                          <td className="text-right erp-amount erp-bold">{fmtShort(inv.net ?? 0)}</td>
                          {/* transfer_status — UYGULAMA ÖZEL */}
                          <td className="text-center">{transferBadge(inv.transfer_status ?? 'Bekliyor')}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
                </>
              )}

            {(activeTab === 'pool' || activeTab === 'viewer') && activeInvoice && (
            <div className="erp-section erp-section-lines" style={{ order: activeTab === 'viewer' ? 2 : 1 }}>
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
                  <span className="erp-section-count">{lines.length} kalem</span>
                )}
              </div>

              <div className="erp-table-wrap">
                {!activeInvoice ? (
                  <div className="erp-empty" style={{ color: '#9ca3af' }}>
                    {activeTab === 'viewer'
                      ? '↑ Görüntülenen firma listesinden bir fatura seçin.'
                      : '↑ Kalemlerini görüntülemek için yukarıdan bir fatura seçin.'}
                  </div>
                ) : linesLoading ? (
                  <div className="erp-empty">⟳ Kalemler yükleniyor...</div>
                ) : (
                  <table className="erp-table">
                    <thead>
                      <tr>
                        <th style={{ width: 30 }}>
                          <input
                            type="checkbox"
                            className="erp-cb"
                            title="Aktarılmamış tüm kalemleri seç"
                            checked={
                              lines.filter(l => l.transfer_status !== 'Aktarildi').length > 0 &&
                              lines
                                .filter(l => l.transfer_status !== 'Aktarildi')
                                .every(l => selectedKalemKeys.includes(normalizeLineKey(l.key)))
                            }
                            onChange={toggleAllLines}
                          />
                        </th>
                        {/* Standart scf_fatura_kalemi_liste_view kolonları */}
                        <th style={{ width: 40 }}>SIRA</th>
                        <th>STOK/HİZMET KODU</th>
                        <th>AÇIKLAMA</th>
                        <th>BİRİM</th>
                        <th className="text-right">MİKTAR</th>
                        <th className="text-right">BİRİM FİYATI</th>
                        <th className="text-right">İNDİRİM</th>
                        <th className="text-right">TUTARI</th>
                        <th className="text-right">KDV%</th>
                        <th className="text-right">KDV TUTAR</th>
                        <th>DEPO</th>
                        <th>ŞUBELER</th>
                        <th>NOT</th>
                        {/* Uygulama özel kolonlar */}
                        <th className="text-center erp-th-custom">DURUM ★</th>
                        <th className="erp-th-custom">HEDEF ★</th>
                      </tr>
                    </thead>
                    <tbody>
                      {lines.map(line => {
                        const checked  = selectedKalemKeys.includes(normalizeLineKey(line.key));
                        const status   = line.transfer_status;
                        const done     = status === 'Aktarildi';
                        const rowCls   = done ? 'erp-row-done'
                                       : checked ? 'erp-row-selected' : 'erp-row';
                        return (
                          <tr
                            key={line.key || line.sirano}
                            className={rowCls}
                            onClick={() => !done && toggleLine(line.key)}

                            title={done ? 'Bu kalem zaten aktarılmış' : 'Seçmek için tıklayın'}
                          >
                            <td className="text-center">
                              <input
                                type="checkbox"
                                className="erp-cb"
                                checked={checked}
                                disabled={done}
                                onChange={() => toggleLine(line.key)}
                                onClick={e => e.stopPropagation()}
                              />
                            </td>
                            {/* ... diğer kolonlar ... */}
                            <td className="text-center erp-muted erp-cell-std">{line.sirano}</td>
                            <td className="erp-mono erp-fisno erp-cell-std">{line.stokhizmetkodu}</td>
                            <td style={{ fontWeight: (checked || done) ? 600 : 400, minWidth: 160 }}>{line.stokhizmetaciklama}</td>
                            <td className="erp-muted">{line.birim ?? '—'}</td>
                            <td className="text-right">{(line.miktar ?? 0).toLocaleString('tr-TR')}</td>
                            <td className="text-right erp-amount">{fmtShort(line.birimfiyati ?? 0)}</td>
                            <td className="text-right erp-muted">
                              {(line.indirimtoplam ?? 0) > 0 ? fmtShort(line.indirimtoplam ?? 0) : '—'}
                            </td>
                            <td className="text-right erp-amount erp-bold">{fmtShort(line.tutari ?? 0)}</td>
                            <td className="text-right">{(line.kdv ?? 0) > 0 ? `%${line.kdv}` : '—'}</td>
                            <td className="text-right erp-muted">{(line.kdvtutari ?? 0) > 0 ? fmtShort(line.kdvtutari ?? 0) : '—'}</td>
                            <td className="erp-muted erp-cell-std">{line.depoadi ?? '—'}</td>
                            <td className="erp-muted erp-cell-std">
                              {(line.dinamik_subeler_normalized || line.dinamik_subeler_raw || '—') as any}
                            </td>
                            <td className="erp-muted erp-cell-std">{line.note ?? '—'}</td>

                            <td className="text-center">
                              {transferBadge(line.transfer_status ?? 'Bekliyor')}
                            </td>

                            <td>
                              {done && line.target_firma_kodu ? (
                                <span className="erp-hedef-tag">
                                  {line.target_firma_kodu}
                                  {line.target_sube_kodu && ` / ${line.target_sube_kodu}`}
                                  {line.target_donem_kodu && ` [${line.target_donem_kodu}]`}
                                </span>
                              ) : (
                                <span className="erp-no-target">—</span>
                              )}
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

            {/* ── 3. İŞLEM GÜNLÜĞÜ ──────────────────────────────────────── */}
            {activeTab === 'pool' && logs.length > 0 && (
              <div className="erp-section erp-section-log">
                <div className="erp-section-header">
                  <span className="erp-section-title">📋 İşlem Günlüğü</span>
                  <button
                    className="erp-btn erp-btn-ghost"
                    style={{ fontSize: 10, padding: '1px 8px', height: 22 }}
                    onClick={() => setLogs([])}
                  >
                    Temizle
                  </button>
                </div>
                <div className="erp-log-panel">
                  {logs.map((l, i) => (
                    <div key={i} className={`erp-log-row ${logCls(l.status)}`}>
                      <span className="erp-log-icon">{logIcon(l.status)}</span>
                      {l.stok_hizmet_kodu && <strong>{l.stok_hizmet_kodu}</strong>}
                      {l.message}
                    </div>
                  ))}
                </div>
              </div>
            )}

              {activeTab === 'viewer' && (
                <div
                  className={`erp-section erp-section-fill erp-section-viewer ${activeInvoice ? 'erp-section-viewer--split' : 'erp-section-viewer--full'}`}
                  style={{ order: 1 }}
                >
                  <div className="erp-section-header">
                    <span className="erp-section-title">
                      🧾 Görüntülenecek Firma Faturaları
                      {activeViewerTab ? ` — ${activeViewerTab.firmaAdi}` : ''}
                    </span>
                    <span className="erp-section-count">{viewerRows.length} kayıt</span>
                  </div>
                  {viewerTabs.length > 0 && (
                    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 10, alignItems: 'center' }}>
                      {viewerTabs.map(t => {
                        const sel = t.id === activeViewerTabId;
                        return (
                          <div
                            key={t.id}
                            role="tab"
                            style={{
                              display: 'inline-flex',
                              alignItems: 'center',
                              gap: 6,
                              padding: '4px 10px',
                              borderRadius: 8,
                              border: sel ? '1px solid #2563eb' : '1px solid #e5e7eb',
                              background: sel ? '#eff6ff' : '#fff',
                              cursor: 'pointer',
                              fontSize: 12,
                            }}
                            onClick={() => selectViewerTab(t)}
                          >
                            <span>{t.firmaAdi} · dönem {t.donemKodu}</span>
                            <button
                              type="button"
                              className="erp-btn erp-btn-ghost"
                              style={{ fontSize: 10, padding: '0 6px', minHeight: 20 }}
                              title="Sekmeyi kapat"
                              onClick={e => closeViewerTab(t.id, e)}
                            >
                              ×
                            </button>
                          </div>
                        );
                      })}
                    </div>
                  )}
                  <div className="erp-table-wrap">
                    <table className="erp-table">
                      <thead>
                        <tr>
                          <th>FATURA NO</th>
                          <th>FİŞ NO</th>
                          <th>TARİH</th>
                          <th>TÜR</th>
                          <th>CARİ KOD</th>
                          <th>CARİ ÜNVAN</th>
                          <th className="text-right">NET</th>
                        </tr>
                      </thead>
                      <tbody>
                        {viewerRows.length === 0 && (
                          <tr>
                            <td colSpan={7} className="erp-empty">
                              {viewerInvoicesLoading
                                ? 'Yükleniyor...'
                                : (!viewerFirmaKodu || !viewerDonemKodu
                                  ? 'Soldan Görüntülenecek Firma + Dönem seçip “Listele” ile yeni sekme açın.'
                                  : 'Bu sekmede kayıt yok veya henüz liste çekilmedi.')}
                            </td>
                          </tr>
                        )}
                        {viewerRows.map(inv => (
                          <tr
                            key={`viewer_${activeViewerTabId}_${inv.key}`}
                            className={activeInvoice?.key === inv.key ? 'erp-row-active erp-row' : 'erp-row'}
                            style={{ cursor: 'pointer' }}
                            onClick={() => {
                              const prevK = activeInvoice ? Number(activeInvoice.key) : 0;
                              const nextK = Number(inv.key);
                              const prevOk = Number.isFinite(prevK) && prevK > 0;
                              const nextOk = Number.isFinite(nextK) && nextK > 0;
                              if (prevOk && nextOk && prevK !== nextK) {
                                resetTargetTransferContext();
                              }
                              setActiveInvoice(inv);
                              const key = Number(inv.key);
                              setSelectedInvoiceKey(Number.isFinite(key) ? key : null);
                              setSelectedKalemKeys([]);
                              setLines([]);
                              if (Number.isFinite(key) && activeViewerTab) {
                                void fetchInvoiceDetailByKey(key, inv.fisno, {
                                  firmaKodu: activeViewerTab.firmaKodu,
                                  donemKodu: activeViewerTab.donemKodu,
                                });
                              }
                            }}
                            title="Kalemleri yükle"
                          >
                            <td className="erp-fisno">{inv.belgeno2 || inv.belgeno || '—'}</td>
                            <td className="erp-muted">{inv.fisno ?? '—'}</td>
                            <td className="erp-date">{inv.tarih ? new Date(inv.tarih).toLocaleDateString('tr-TR') : '—'}</td>
                            <td className="erp-muted erp-cell-std">{inv.turuack ?? '—'}</td>
                            <td className="erp-mono">{inv.carikartkodu ?? '—'}</td>
                            <td className="erp-cari">{inv.cariunvan ?? '—'}</td>
                            <td className="text-right erp-amount erp-bold">{fmtShort(inv.net ?? 0)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <div style={{ display: 'flex', justifyContent: 'flex-end', padding: '8px 0' }}>
                    <button
                      className="erp-btn erp-btn-secondary"
                      disabled={viewerInvoicesLoading || !viewerHasMore || viewerRows.length === 0 || !activeViewerTabId}
                      onClick={async () => {
                        await fetchViewerInvoices('appendActive');
                      }}
                    >
                      {viewerHasMore ? 'Daha fazla' : 'Tümü gösterildi'}
                    </button>
                  </div>
                </div>
              )}

              {activeTab === 'last' && (
                <div className="erp-section erp-section-fill erp-section-viewer">
                  <div className="erp-section-header">
                    <span className="erp-section-title">🧾 Son Aktarılan Kayıt</span>
                    <span className="erp-section-count">
                      {(viewerLastInvoice ? 1 : 0) + (lastTransfers.length > 0 ? lastTransfers.length : 0)} kayıt
                    </span>
                  </div>
                  <div className="erp-table-wrap">
                    <table className="erp-table">
                      <thead>
                        <tr>
                          <th>FATURA NO</th>
                          <th>FİŞ NO</th>
                          <th>TARİH</th>
                          <th>TÜR</th>
                          <th>CARİ KOD</th>
                          <th>CARİ ÜNVAN</th>
                          <th className="text-right">NET</th>
                        </tr>
                      </thead>
                      <tbody>
                        {!viewerLastInvoice && lastTransfers.length === 0 && (
                          <tr>
                            <td colSpan={7} className="erp-empty">
                              {viewerLastLookupLoading ? 'Yükleniyor...' : 'Henüz doğrulanmış son aktarılan kayıt yok.'}
                            </td>
                          </tr>
                        )}
                        {!viewerLastInvoice && lastTransfers.length > 0 && lastTransfers.map(t => (
                          <tr key={`last_fallback_${t.createdInvoiceKey}`} className="erp-row">
                            <td className="erp-fisno">{`KEY#${t.createdInvoiceKey}`}</td>
                            <td className="erp-muted">{t.targetFisNo ?? '—'}</td>
                            <td className="erp-date">—</td>
                            <td className="erp-muted erp-cell-std">FATURA</td>
                            <td className="erp-mono">{t.targetCariKod ?? '—'}</td>
                            <td className="erp-cari">{t.targetCariUnvan ?? '—'}</td>
                            <td className="text-right erp-amount erp-bold">—</td>
                          </tr>
                        ))}
                        {viewerLastInvoice && (
                          <tr key={`last_${viewerLastInvoice.key}`} className="erp-row">
                            <td className="erp-fisno">{viewerLastInvoice.belgeno2 || viewerLastInvoice.belgeno || '—'}</td>
                            <td className="erp-muted">{viewerLastInvoice.fisno ?? '—'}</td>
                            <td className="erp-date">{viewerLastInvoice.tarih ? new Date(viewerLastInvoice.tarih).toLocaleDateString('tr-TR') : '—'}</td>
                            <td className="erp-muted erp-cell-std">{viewerLastInvoice.turuack ?? '—'}</td>
                            <td className="erp-mono">{viewerLastInvoice.carikartkodu ?? '—'}</td>
                            <td className="erp-cari">{viewerLastInvoice.cariunvan ?? '—'}</td>
                            <td className="text-right erp-amount erp-bold">{fmtShort(viewerLastInvoice.net ?? 0)}</td>
                          </tr>
                        )}
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
                  <strong>{effectiveInvoiceKeys.length > 0 ? `${effectiveInvoiceKeys.length} adet` : '—'}</strong>
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
                  <strong className={selectedKalemKeys.length > 0 ? 'erp-summary-highlight' : ''}>
                    {selectedKalemKeys.length > 0
                      ? `${selectedKalemKeys.length} adet`
                      : (isBulkTransfer ? 'Tümü (otomatik)' : 'Tümü (otomatik)')}
                  </strong>
                </div>
                {selectedKalemKeys.length === 0 ? (
                  <div style={{ fontSize: 11, color: '#0f766e' }}>
                    Kalem seçilmedi. Aktarım sırasında <strong>tüm kalemler otomatik seçilecek</strong>.
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

              {/* Disabled açıklaması */}
              {!canTransfer && (
                <div className="erp-hint-box">
                  {transferBlockers.map(reason => (
                    <div key={reason}>• {reason}</div>
                  ))}
                </div>

              )}

              {/* ANA AKSİYON BUTONU */}
              <button
                className={`erp-btn erp-btn-transfer ${canTransfer ? 'erp-btn-transfer-active' : ''}`}
                onClick={handleTransfer}
                disabled={!canTransfer}
                title={canTransfer ? 'Aktarımı başlat' : 'Kalem, firma, şube ve dönem seçin'}
              >
                {transferring
                  ? '⟳ Aktarılıyor...'
                  : canTransfer
                    ? (willAutoSelectAllLines ? '➤ TÜM KALEMLERİ AKTAR' : `➤ ${selectedKalemKeys.length} KALEMİ AKTAR`)
                    : '➤ AKTARIMI BAŞLAT'
                }
              </button>

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
                          await fetchLastTransferredInvoice(lastTransfer.createdInvoiceKey, lastTransfer.targetFirmaKodu, lastTransfer.targetDonemKodu);
                          pushLog({ source_kalem_key: '', status: 'success', message: `Hedef fatura doğrulandı. key=${lastTransfer.createdInvoiceKey} fisno=${fisno ?? '—'}`, was_duplicate_override: false });
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
