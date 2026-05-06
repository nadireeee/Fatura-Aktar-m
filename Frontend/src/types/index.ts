// ══════════════════════════════════════════════════════════════════════════════
// DİA ERP — Havuz Fatura Aktarım Modülü — TypeScript Tip Tanımları
//
// Alan ayrımı:
//   Standart DİA alanları: scf_fatura_liste_view, scf_fatura_kalemi_liste_view'dan gelir
//   Uygulama özel alanlar: [ÖZEL] yorumuyla işaretlenmiştir
// ══════════════════════════════════════════════════════════════════════════════

// ── Enum'lar — UYGULAMA ÖZEL ──────────────────────────────────────────────────
export type TransferStatus     = 'Bekliyor' | 'Kismi' | 'Aktarildi' | 'Hata';
export type DuplicateRiskLevel = 'Yok' | 'Dusuk' | 'Yuksek' | 'Kesin';
export type MappingStatus      = 'Eslenmedi' | 'Kismi' | 'Tamam';

// ══════════════════════════════════════════════════════════════════════════════
// REAL DİA (Okuma) DTO'ları
// ══════════════════════════════════════════════════════════════════════════════

export interface ISourceCompanyDto {
  firma_kodu: number;
  firma_adi: string;
}

export interface ISourcePeriodDto {
  key: number;
  donemkodu: number;
  gorunenkod: string;
  ontanimli: boolean;
  baslangic_tarihi?: string;
  bitis_tarihi?: string;
}

export interface ISourceBranchDto {
  key: number;
  subeadi: string;
}

export interface ISourceDepotDto {
  key: number;
  depoadi: string;
}

export interface ISourceCurrencyDto {
  key: number;
  kodu: string;
  adi: string;
}

export interface IDefaultSourceContextDto {
  defaultSourceFirmaKodu: number;
  defaultSourceDonemKodu: number;
  defaultSourceSubeKey: number;
}

export interface IPoolContextDto {
  poolFirmaKodu: number;
  poolFirmaAdi: string;
}

export interface ITargetResolveResultDto {
  targetFirmaKodu: number;
  targetFirmaAdi: string;
  targetSubeKey: number;
  targetSubeAdi: string;
  targetDepoKey: number;
  targetDepoAdi: string;
  targetDonemKodu: number;
  targetDonemKey: number;
  targetDonemLabel: string;
  autoSelected: boolean;
  fallbackUsed: boolean;
  fallbackReason?: string;
}

export interface IInvoiceListRow {
  key: string; // DIA _key (numeric) -> string
  fisno?: string;
  belgeno?: string;
  belgeno2?: string;
  tarih?: string;
  turu?: number;
  turuack?: string;
  turu_kisa?: string;
  carikartkodu?: string;
  cariunvan?: string;
  sourcesubeadi?: string;
  sourcedepoadi?: string;
  destsubeadi?: string;
  destdepoadi?: string;
  firmaadi?: string;
  dovizturu?: string;
  toplam?: number;
  toplamkdv?: number;
  net?: number;
  iptal?: boolean;
  odemeplani?: string;
  odemeplaniack?: string;
  projekodu?: string;
  projeaciklama?: string;

  // UI eski alanları için (okuma aşamasında sabit/boş)
  transfer_status?: TransferStatus;
  duplicate_risk?: DuplicateRiskLevel;
  bekleyen_kalem_sayisi?: number;
  muhasebelesme?: boolean;
  dovizadi?: string;
  sourcesubeadi_display?: string;
  hasLineBranchSelection?: boolean;
  hasHeaderOnlyBranch?: boolean;
  effectiveTransferType?: 'FATURA' | 'VİRMAN' | string;
}

export interface IInvoiceLineListRow {
  key: string;
  sirano?: number;
  kalemturu?: number;
  stokhizmetkodu?: string;
  stokhizmetaciklama?: string;
  birim?: string;
  miktar?: number;
  birimfiyati?: number;
  sonbirimfiyati?: number;
  tutari?: number;
  kdv?: number;
  kdvtutari?: number;
  indirimtoplam?: number;
  depoadi?: string;
  note?: string;
  note2?: string;
  projekodu?: string;
  projeaciklama?: string;

  dinamik_subeler_raw?: string;
  dinamik_subeler_normalized?: string;

  // UI seçim alanı
  transfer_status?: TransferStatus;
  target_firma_kodu?: string;
  target_sube_kodu?: string;
  target_donem_kodu?: string;
}

export interface IInvoiceDetailDto {
  key: string;
  fisno?: string;
  tarih?: string;
  kalemler: IInvoiceLineListRow[];
}

export interface IInvoiceTransferRequestDto {
  sourceFirmaKodu: number;
  sourceDonemKodu: number;
  sourceSubeKey?: number;
  sourceDepoKey?: number;
  sourceInvoiceKey: number;
  selectedKalemKeys: number[];
  selectedLineSnapshots?: Array<{
    sourceLineKey?: number;
    stokKartKodu?: string;
    aciklama?: string;
    miktar?: number;
    birimFiyati?: number;
    tutar?: number;
  }>;
  targetFirmaKodu: number;
  // backend otomatik çözümleyebilir (0/undefined gönderilebilir)
  targetDonemKodu?: number;
  targetSubeKey?: number;
  targetDepoKey?: number;
}

export interface IInvoiceTransferResultDto {
  success: boolean;
  message: string;
  createdInvoiceKey?: number;
  createdTargetType?: string; // FATURA
  createdVerified?: boolean;
  createdVerifyMessage?: string;
  targetFirmaKodu?: number;
  targetDonemKodu?: number;
  targetSubeKey?: number;
  targetDepoKey?: number;
  createdKalemKeys: number[];
  transferredLineCount: number;
  duplicateSkippedCount: number;
  errors: string[];
  failureStage?: string;
  failureCode?: string;
  details?: string;
  diaPayload?: unknown;
  diaResponse?: string;
  traceId?: string;
}

// ══════════════════════════════════════════════════════════════════════════════
// ÜST GRİD — scf_fatura_liste_view kaynaklı alanlar
// ══════════════════════════════════════════════════════════════════════════════

export interface IInvoiceListRow_OLD {
  // Sistem (standart)
  key: string;
  _cdate?: string;
  _user?:  string;

  // Fatura kimlik (standart scf_fatura_liste_view)
  fisno:    string;
  belgeno:  string;
  belgeno2?: string;
  tarih:    string;
  saat?:    string;
  turu_txt: string;
  kategori?: string;

  // Cari (standart)
  carikartkodu:  string;
  cariunvan:     string;
  carivergitcno?: string;

  // Firma / Şube (standart scf_fatura_liste_view)
  firmaadi:      string;
  sourcesubeadi: string;
  sourcedepoadi?: string;
  destsubeadi?:  string;
  destdepoadi?:  string;

  // Döviz (standart)
  dovizadi:  string;
  dovizkuru: number;

  // Finansal toplamlar (standart)
  toplam:             number;
  toplamdvz:          number;
  toplamkdv:          number;
  toplamkdvdvz:       number;
  toplamkdvtevkifati: number;
  toplamindirim:      number;
  toplammasraf:       number;
  toplamov:           number;
  net:    number;
  netdvz: number;

  // Durum (standart)
  iptal:           boolean;
  kilitli:         boolean;
  muhasebelesme:   boolean;
  kapanmadurumu:   number;
  kasadurum:       number;
  efatura_durum_txt?:  string;
  efaturasenaryosu?:   string;
  earsiv_durum?:       string;

  // Kullanıcı (standart)
  kullaniciadi?: string;
  satiselemani?: string;
  vadegun:       number;
  aciklama1?:    string;

  // ── UYGULAMA ÖZEL alanlar ────────────────────────────────────────────────
  transfer_status:      TransferStatus;      // [ÖZEL]
  duplicate_risk:       DuplicateRiskLevel;  // [ÖZEL]
  transfer_log_id?:     string;              // [ÖZEL]
  bekleyen_kalem_sayisi: number;             // [ÖZEL - hesaplanan]
}

// ══════════════════════════════════════════════════════════════════════════════
// ALT GRİD — scf_fatura_kalemi_liste_view kaynaklı alanlar
// ══════════════════════════════════════════════════════════════════════════════

export interface IInvoiceLineListRow_OLD {
  // Sistem (standart)
  key: string;

  faturakey: string; // FaturaKey


  // Sıra & Tür (standart)
  sirano:    number;
  kalemturu: number;

  // Stok / Hizmet (standart)
  stokhizmetkodu:     string;
  stokhizmetaciklama: string;
  stokkartmarka?:     string;
  kalemozelkodu?:     string;

  // Birim & Miktar (standart)
  birimkodu:    string;
  anabirimkodu?: string;
  miktar:       number;
  anamiktar:    number;

  // Fiyat (standart)
  birimfiyati:        number;
  sonbirimfiyati:     number;
  yerelbirimfiyati:   number;
  tutari:             number;
  tutarisatirdovizi:  number;
  dovizkuru:          number;
  dovizadi:           string;

  // İndirim (standart)
  indirim1:         number;
  indirim2:         number;
  indirim3:         number;
  indirim4:         number;
  indirim5:         number;
  indirimtoplam:    number;
  indirimtutari:    number;
  kdvdahilindirimtoplamtutar: number;

  // KDV (standart)
  kdv:               number;
  kdvtutari:         number;
  kdvdurumu:         number;
  kdvtevkifatorani:  number;
  kdvtevkifattutari: number;

  // ÖTV (standart)
  ovtutartutari:  number;
  ovtutartutari2: number;
  ovkdvoran:      number;
  ovkdvtutari:    number;
  ovorantutari:   number;
  ovtoplamtutari: number;

  // Konum (standart)
  depoadi?:              string;
  karsidepoadi?:         string;
  masrafmerkezikodu?:    string;
  masrafmerkeziaciklama?: string;
  projekodu?:            string;
  projeaciklama?:        string;

  // İrsaliye / Sipariş (standart)
  irsaliyeno?:   string;
  irsaliyetarih?: string;
  siparisno?:    string;
  siparistarih?:  string;

  // Ödeme planı (standart)
  odemeplanikodu?:    string;
  odemeplaniaciklama?: string;

  // Notlar (standart)
  note?:  string;
  note2?: string;

  // Özel alanlar (standart scf_fatura_kalemi_liste_view)
  ozelalan1?: string;
  ozelalan2?: string;
  ozelalan3?: string;
  ozelalan4?: string;
  ozelalan5?: string;

  // Müstahsil (standart)
  cari_mustahsil_kodu?:   string;
  cari_mustahsil_unvan?:  string;

  // Maliyet (standart)
  maliyetfaturano?: string;
  maliyetstokkodu?: string;

  // İade (standart)
  iadeanamiktar:   number;
  iadekalanmiktar: number;

  // Ağırlık/Hacim (standart)
  toplambrutagirlik: number;
  toplamnetagirlik:  number;
  toplambruthacim:   number;
  toplamnethacim:    number;

  // Kullanıcı (standart)
  kullaniciadi?: string;
  satiselemaniaciklama?: string;

  // ── UYGULAMA ÖZEL alanlar ────────────────────────────────────────────────
  is_selected:    boolean;          // [ÖZEL] UI seçim
  transfer_status: TransferStatus;  // [ÖZEL]
  mapping_status:  MappingStatus;   // [ÖZEL]
  duplicate_risk:  DuplicateRiskLevel; // [ÖZEL]

  // Aktarım sonrası dolan hedef bilgileri — standart DİA kalemi kolonu değil
  target_firma_key?:  string;  // [ÖZEL] sis_firma._key
  target_firma_kodu?: string;  // [ÖZEL] görüntü amaçlı
  target_sube_key?:   string;  // [ÖZEL] sis_sube._key
  target_sube_kodu?:  string;  // [ÖZEL] görüntü amaçlı
  target_donem_key?:  string;  // [ÖZEL] sis_donem._key
  target_donem_kodu?: string;  // [ÖZEL] görüntü amaçlı
  is_manual_override: boolean; // [ÖZEL]
}

// ══════════════════════════════════════════════════════════════════════════════
// DÖNEM DTO — sis_donem kaynaklı
// ══════════════════════════════════════════════════════════════════════════════

export interface ITargetPeriodDto {
  key: string;
  // sis_donem._key
  donemkodu:   string;  // sis_donem.donemkodu
  gorunenkod:  string;  // sis_donem.gorunenkod
  baslangic:   string;  // sis_donem.baslangic
  bitis:       string;  // sis_donem.bitis
  aktif:       boolean; // sis_donem.aktif
  arsiv:       boolean; // sis_donem.arsiv
  ontanimli:   boolean; // sis_donem.ontanimli
  firma_key?:  string;  // sis_donem._key_sis_firma
}

// ══════════════════════════════════════════════════════════════════════════════
// ŞUBE DTO — sis_sube kaynaklı
// ══════════════════════════════════════════════════════════════════════════════

export interface ITargetBranchDto {
  key:       string;  // sis_sube._key
  subekodu:   string;  // sis_sube.subekodu
  subeadi:    string;  // sis_sube.subeadi
  aktif:      boolean; // sis_sube.aktif
  merkezmi:   boolean; // sis_sube.merkezmi
  firma_key?: string;  // sis_sube._key_sis_firma
}

// ══════════════════════════════════════════════════════════════════════════════
// FİRMA DTO — sis_kullanici_firma_parametreleri kaynaklı
// ══════════════════════════════════════════════════════════════════════════════

export interface IFirmaDto {
  firma_key:  string;
  firma_kodu: string;  // sis_kullanici_firma_parametreleri.firma
  firma_adi:  string;
}

// ══════════════════════════════════════════════════════════════════════════════
// TRANSFER İSTEĞİ — UYGULAMA ÖZEL
// ══════════════════════════════════════════════════════════════════════════════

export interface ITransferRequest {
  // Standart DİA referansları
  source_fatura_key: string;      // scf_fatura._key
  source_firma_key:  string;
  source_sube_key:   string;      // sis_sube._key
  selected_kalem_keys: string[];  // scf_fatura_kalemi._key listesi

  // Hedef — UYGULAMA ÖZEL (standart DİA kalemi kolonu değil)
  target_firma_key:  string;  // [ÖZEL] sis_firma._key
  target_firma_kodu: string;  // [ÖZEL]
  target_sube_key:   string;  // [ÖZEL] sis_sube._key
  target_sube_kodu:  string;  // [ÖZEL]
  target_donem_key:  string;  // [ÖZEL] sis_donem._key
  target_donem_kodu: string;  // [ÖZEL]

  // Kontrol — UYGULAMA ÖZEL
  allow_duplicate_override: boolean;
  transferred_by: string;
}

// ══════════════════════════════════════════════════════════════════════════════
// TRANSFER SONUCU — UYGULAMA ÖZEL
// ══════════════════════════════════════════════════════════════════════════════

export interface ITransferLogEntry {
  source_kalem_key:      string;
  stok_hizmet_kodu?:     string;
  stok_hizmet_aciklama?: string;
  new_kalem_key?:        string;
  status:                'success' | 'duplicate' | 'error';
  message:               string;
  was_duplicate_override: boolean;
}

export interface ITransferResult {
  success:           boolean;
  message:           string;
  new_fatura_key?:   string;  // Hedefde oluşturulan scf_fatura._key
  new_fatura_no?:    string;  // Hedefde oluşturulan fisno
  total_success:     number;
  total_duplicate:   number;
  total_error:       number;
  process_logs:      ITransferLogEntry[];
  transfer_log_id?:  string;  // [ÖZEL]
  transferred_at:    string;
}
