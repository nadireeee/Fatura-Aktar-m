using DiaErpIntegration.API.Models;

namespace DiaErpIntegration.API.Services
{
    /// <summary>
    /// Mock fatura deposu. Gerçek DİA entegrasyonunda bu katman DİA WS sorgularına dönüşür.
    /// 
    /// Kolon adları: scf_fatura_liste_view ve scf_fatura_kalemi_liste_view gerçek alanlarına göre.
    /// Uygulama özel alanlar [NotMapped] ile işaretlenmiştir.
    /// </summary>
    public class MockDataRepository
    {
        // Üst grid verileri — scf_fatura_liste_view kaynaklı
        public List<InvoiceListRowDto> Invoices { get; }

        // Alt grid verileri — scf_fatura_kalemi_liste_view kaynaklı (fatura key'e göre gruplu)
        public Dictionary<string, List<InvoiceLineListRowDto>> InvoiceLines { get; }

        // Dönem listesi — sis_donem kaynaklı
        public List<TargetPeriodDto> Donemler { get; }

        // Şube listesi — sis_sube kaynaklı
        public List<TargetBranchDto> Subeler { get; }

        // Firma listesi — sis_kullanici_firma_parametreleri kaynaklı
        public List<FirmaDto> Firmalar { get; }

        public MockDataRepository()
        {
            Firmalar = SeedFirmalar();
            Subeler  = SeedSubeler();
            Donemler = SeedDonemler();
            Invoices = SeedInvoices();
            InvoiceLines = SeedInvoiceLines();

            // Başlangıçta aktarılmış kalemlerin duplicate set'ini hazırla
            lock (_lock)
            {
                if (_transferredKeys.Count == 0)
                    foreach (var k in GetInitialDuplicateKeys())
                        _transferredKeys.Add(k);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Duplicate tracking (uygulama özel)
        // ══════════════════════════════════════════════════════════════════════

        private static readonly HashSet<string> _transferredKeys = new();
        private static readonly object _lock = new();

        public bool IsDuplicate(string compositeKey)
        {
            lock (_lock) { return _transferredKeys.Contains(compositeKey); }
        }

        public void MarkAsTransferred(string compositeKey)
        {
            lock (_lock) { _transferredKeys.Add(compositeKey); }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Aktarım sonrası kaynak veriyi güncelle (mock için)
        // ══════════════════════════════════════════════════════════════════════

        public void MarkSourceLineAsTransferred(
            string sourceFaturaKey,
            string sourceKalemKey,
            string targetFirmaKey,
            string targetFirmaKodu,
            string targetSubeKey,
            string targetSubeKodu,
            string targetDonemKey,
            string targetDonemKodu)
        {
            if (InvoiceLines.TryGetValue(sourceFaturaKey, out var lines))
            {
                var line = lines.FirstOrDefault(l => l.Key == sourceKalemKey);
                if (line != null)
                {
                    line.TransferStatus = TransferStatus.Aktarildi;
                    line.MappingStatus = MappingStatus.Tamam;
                    line.DuplicateRisk = DuplicateRiskLevel.Yok;

                    line.TargetFirmaKey = targetFirmaKey;
                    line.TargetFirmaKodu = targetFirmaKodu;
                    line.TargetSubeKey = targetSubeKey;
                    line.TargetSubeKodu = targetSubeKodu;
                    line.TargetDonemKey = targetDonemKey;
                    line.TargetDonemKodu = targetDonemKodu;
                }
            }

            RecalculateInvoiceStatus(sourceFaturaKey);
        }

        public void RecalculateInvoiceStatus(string sourceFaturaKey)
        {
            var inv = Invoices.FirstOrDefault(i => i.Key == sourceFaturaKey);
            if (inv == null) return;

            if (!InvoiceLines.TryGetValue(sourceFaturaKey, out var lines) || lines.Count == 0)
            {
                inv.BekleyenKalemSayisi = 0;
                inv.TransferStatus = TransferStatus.Bekliyor;
                return;
            }

            var pending = lines.Count(l => l.TransferStatus != TransferStatus.Aktarildi);
            inv.BekleyenKalemSayisi = pending;

            if (pending == 0)
                inv.TransferStatus = TransferStatus.Aktarildi;
            else if (pending == lines.Count)
                inv.TransferStatus = TransferStatus.Bekliyor;
            else
                inv.TransferStatus = TransferStatus.Kismi;
        }

        private IEnumerable<string> GetInitialDuplicateKeys()
        {
            foreach (var lines in InvoiceLines.Values)
                foreach (var line in lines.Where(l => l.TransferStatus == TransferStatus.Aktarildi))
                    yield return TransferService.BuildCompositeKey(
                        line.FaturaKey, line.Key,
                        line.TargetFirmaKey ?? "", line.TargetSubeKey ?? "", line.TargetDonemKey ?? "");
        }

        // ══════════════════════════════════════════════════════════════════════
        // SEED — Firmalar (sis_kullanici_firma_parametreleri)
        // ══════════════════════════════════════════════════════════════════════

        private static List<FirmaDto> SeedFirmalar() => new()
        {
            new() { FirmaKey = "firma-001", FirmaKodu = "001", FirmaAdi = "Havuz Firma A.Ş." },
            new() { FirmaKey = "firma-002", FirmaKodu = "002", FirmaAdi = "Beta Holding A.Ş." },
            new() { FirmaKey = "firma-003", FirmaKodu = "003", FirmaAdi = "Gamma Ticaret Ltd. Şti." },
        };

        // ══════════════════════════════════════════════════════════════════════
        // SEED — Şubeler (sis_sube)
        // ══════════════════════════════════════════════════════════════════════

        private static List<TargetBranchDto> SeedSubeler() => new()
        {
            // firma-001 şubeleri (kaynak havuz)
            new() { Key = "sube-001-mk",  SubeKodu = "MK",  SubeAdi = "Merkez",         Aktif = true, MerkezMi = true,  FirmaKey = "firma-001" },
            new() { Key = "sube-001-an",  SubeKodu = "AN",  SubeAdi = "Anadolu Deposu", Aktif = true, MerkezMi = false, FirmaKey = "firma-001" },
            new() { Key = "sube-001-hv",  SubeKodu = "HV",  SubeAdi = "Havaalanı Şb.",  Aktif = true, MerkezMi = false, FirmaKey = "firma-001" },
            new() { Key = "sube-001-gm",  SubeKodu = "GM",  SubeAdi = "Genel Müdürlük", Aktif = true, MerkezMi = false, FirmaKey = "firma-001" },
            new() { Key = "sube-001-ur",  SubeKodu = "UR",  SubeAdi = "Üretim Tesisi",  Aktif = true, MerkezMi = false, FirmaKey = "firma-001" },
            new() { Key = "sube-001-ts",  SubeKodu = "TS",  SubeAdi = "Teknik Servis",  Aktif = true, MerkezMi = false, FirmaKey = "firma-001" },

            // firma-002 şubeleri (hedef)
            new() { Key = "sube-002-mk",  SubeKodu = "MK",  SubeAdi = "Merkez",         Aktif = true, MerkezMi = true,  FirmaKey = "firma-002" },
            new() { Key = "sube-002-an",  SubeKodu = "AN",  SubeAdi = "Anadolu",         Aktif = true, MerkezMi = false, FirmaKey = "firma-002" },

            // firma-003 şubeleri (hedef)
            new() { Key = "sube-003-mk",  SubeKodu = "MK",  SubeAdi = "Merkez",         Aktif = true, MerkezMi = true,  FirmaKey = "firma-003" },
            new() { Key = "sube-003-ist", SubeKodu = "IST", SubeAdi = "İstanbul Şb.",   Aktif = true, MerkezMi = false, FirmaKey = "firma-003" },
        };

        // ══════════════════════════════════════════════════════════════════════
        // SEED — Dönemler (sis_donem)
        // ══════════════════════════════════════════════════════════════════════

        private static List<TargetPeriodDto> SeedDonemler() => new()
        {
            // firma-002 dönemleri
            new() { Key = "donem-002-2024", DonemKodu = "2024", GorunenKod = "2024 Yılı",
                    Baslangic = new(2024,1,1), Bitis = new(2024,12,31),
                    Aktif = false, Arsiv = false, Ontanimli = false, FirmaKey = "firma-002" },
            new() { Key = "donem-002-2025", DonemKodu = "2025", GorunenKod = "2025 Yılı",
                    Baslangic = new(2025,1,1), Bitis = new(2025,12,31),
                    Aktif = true,  Arsiv = false, Ontanimli = true,  FirmaKey = "firma-002" },

            // firma-003 dönemleri
            new() { Key = "donem-003-2024", DonemKodu = "2024", GorunenKod = "2024 Yılı",
                    Baslangic = new(2024,1,1), Bitis = new(2024,12,31),
                    Aktif = false, Arsiv = false, Ontanimli = false, FirmaKey = "firma-003" },
            new() { Key = "donem-003-2025", DonemKodu = "2025", GorunenKod = "2025 Yılı",
                    Baslangic = new(2025,1,1), Bitis = new(2025,12,31),
                    Aktif = true,  Arsiv = false, Ontanimli = true,  FirmaKey = "firma-003" },
        };

        // ══════════════════════════════════════════════════════════════════════
        // SEED — Fatura Başlıkları (scf_fatura_liste_view)
        // Tüm alan adları gerçek DİA view kolonlarıdır
        // ══════════════════════════════════════════════════════════════════════

        private static List<InvoiceListRowDto> SeedInvoices() => new()
        {
            new() {
                Key = "ftr-2025-101",
                FisNo = "FTR-2025-101", BelgeNo = "BLG-101",
                Tarih = DateTime.Now, Saat = "10:30",
                TuruTxt = "Alış Faturası",
                CariKartKodu = "120.101", CariUnvan = "Aselsan Elektronik Sanayi",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Merkez", SubeKodu = "MK", SourceDepoAdi = "Ana Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 250000m, ToplamKdv = 50000m, Net = 300000m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 2,
            },
            new() {
                Key = "ftr-2025-102",
                FisNo = "FTR-2025-102", BelgeNo = "BLG-102",
                Tarih = DateTime.Now.AddDays(-1), Saat = "14:20",
                TuruTxt = "Hizmet Faturası",
                CariKartKodu = "120.102", CariUnvan = "Türk Havayolları A.O.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Havaalanı Şb.", SubeKodu = "HV", SourceDepoAdi = "Havaalanı Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 45000m, ToplamKdv = 9000m, Net = 54000m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 1,
            },
            new() {
                Key = "ftr-2025-103",
                FisNo = "FTR-2025-103", BelgeNo = "BLG-103",
                Tarih = DateTime.Now.AddDays(-2), Saat = "09:15",
                TuruTxt = "Alış Faturası",
                CariKartKodu = "120.103", CariUnvan = "Koç Sistem A.Ş.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Merkez", SubeKodu = "MK", SourceDepoAdi = "Ana Depo",
                DovizAdi = "USD", DovizKuru = 32.80m,
                Toplam = 12000m, ToplamKdv = 2400m, Net = 14400m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 3,
            },
            new() {
                Key = "ftr-2025-104",
                FisNo = "FTR-2025-104", BelgeNo = "BLG-104",
                Tarih = DateTime.Now.AddDays(-3), Saat = "16:45",
                TuruTxt = "Lojistik Faturası",
                CariKartKodu = "120.104", CariUnvan = "Ekol Lojistik A.Ş.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Anadolu Deposu", SubeKodu = "AN", SourceDepoAdi = "Anadolu Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 18500m, ToplamKdv = 3700m, Net = 22200m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 2,
            },
            new() {
                Key = "ftr-2025-105",
                FisNo = "FTR-2025-105", BelgeNo = "BLG-105",
                Tarih = DateTime.Now.AddDays(-5), Saat = "11:00",
                TuruTxt = "Yazılım Hizmet Faturası",
                CariKartKodu = "120.105", CariUnvan = "Logo Yazılım A.Ş.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Genel Müdürlük", SubeKodu = "GM", SourceDepoAdi = "GM Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 62000m, ToplamKdv = 12400m, Net = 74400m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 2,
            },
            new() {
                Key = "ftr-2025-001",
                FisNo = "FTR-2025-001", BelgeNo = "BLG-001",
                Tarih = new DateTime(2025,1,15), Saat = "09:30",
                TuruTxt = "Alış Faturası",
                CariKartKodu = "120.001", CariUnvan = "Bosch Termoteknik A.Ş.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Merkez", SubeKodu = "MK", SourceDepoAdi = "Ana Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 12500m, ToplamKdv = 2250m, Net = 14750m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 3,
            },
            new() {
                Key = "ftr-2025-002",
                FisNo = "FTR-2025-002", BelgeNo = "BLG-002",
                Tarih = new DateTime(2025,1,18), Saat = "11:15",
                TuruTxt = "Alış Faturası",
                CariKartKodu = "120.002", CariUnvan = "Eczacıbaşı Holding",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Merkez", SubeKodu = "MK", SourceDepoAdi = "Ana Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 34750m, ToplamKdv = 6255m, Net = 41005m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Kismi, BekleyenKalemSayisi = 1,
            },
            new() {
                Key = "ftr-2025-003",
                FisNo = "FTR-2025-003", BelgeNo = "BLG-003",
                Tarih = new DateTime(2025,1,20), Saat = "14:00",
                TuruTxt = "Hizmet Faturası",
                CariKartKodu = "120.003", CariUnvan = "Sabancı Enerji A.Ş.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Anadolu Deposu", SubeKodu = "AN", SourceDepoAdi = "Anadolu Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 8200m, ToplamKdv = 1476m, Net = 9676m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 2,
            },
            new() {
                Key = "ftr-2025-004",
                FisNo = "FTR-2025-004", BelgeNo = "BLG-004",
                Tarih = new DateTime(2025,1,22), Saat = "08:45",
                TuruTxt = "Alış Faturası",
                CariKartKodu = "120.004", CariUnvan = "Shell & Turcas Petrol",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Havaalanı Şb.", SubeKodu = "HV", SourceDepoAdi = "Havaalanı Depo",
                DovizAdi = "USD", DovizKuru = 32.50m,
                Toplam = 29000m, ToplamKdv = 5220m, Net = 34220m,
                Iptal = false, Kilitli = false, Muhasebelesme = true,
                TransferStatus = TransferStatus.Aktarildi, BekleyenKalemSayisi = 0,
            },
            new() {
                Key = "ftr-2025-005",
                FisNo = "FTR-2025-005", BelgeNo = "BLG-005",
                Tarih = new DateTime(2025,1,25), Saat = "10:00",
                TuruTxt = "Gider Faturası",
                CariKartKodu = "120.005", CariUnvan = "Türk Telekom A.Ş.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Merkez", SubeKodu = "MK", SourceDepoAdi = "Ana Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 5600m, ToplamKdv = 1008m, Net = 6608m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 2,
            },
            new() {
                Key = "ftr-2025-006",
                FisNo = "FTR-2025-006", BelgeNo = "BLG-006",
                Tarih = new DateTime(2025,2,3), Saat = "13:30",
                TuruTxt = "Hizmet Faturası",
                CariKartKodu = "120.006", CariUnvan = "Sodexo Hizmet A.Ş.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Üretim Tesisi", SubeKodu = "UR", SourceDepoAdi = "Üretim Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 18900m, ToplamKdv = 3402m, Net = 22302m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Kismi, BekleyenKalemSayisi = 2,
            },
            new() {
                Key = "ftr-2025-007",
                FisNo = "FTR-2025-007", BelgeNo = "BLG-007",
                Tarih = new DateTime(2025,2,10), Saat = "09:00",
                TuruTxt = "Kira Faturası",
                CariKartKodu = "120.007", CariUnvan = "Garanti BBVA Leasing",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Genel Müdürlük", SubeKodu = "GM", SourceDepoAdi = "GM Depo",
                DovizAdi = "EUR", DovizKuru = 35.20m,
                Toplam = 42000m, ToplamKdv = 7560m, Net = 49560m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 3,
            },
            new() {
                Key = "ftr-2025-008",
                FisNo = "FTR-2025-008", BelgeNo = "BLG-008",
                Tarih = new DateTime(2025,2,14), Saat = "15:30",
                TuruTxt = "Bakım Faturası",
                CariKartKodu = "120.008", CariUnvan = "Arçelik Servis",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Teknik Servis", SubeKodu = "TS", SourceDepoAdi = "Servis Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 6750m, ToplamKdv = 1215m, Net = 7965m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 2,
            },
            new() {
                Key = "ftr-2025-009",
                FisNo = "FTR-2025-009", BelgeNo = "BLG-009",
                Tarih = new DateTime(2025,2,18), Saat = "10:45",
                TuruTxt = "Güvenlik Hizmet Faturası",
                CariKartKodu = "120.009", CariUnvan = "Securitas Güvenlik",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Merkez", SubeKodu = "MK", SourceDepoAdi = "Ana Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 16500m, ToplamKdv = 2970m, Net = 19470m,
                Iptal = false, Kilitli = false, Muhasebelesme = true,
                TransferStatus = TransferStatus.Aktarildi, BekleyenKalemSayisi = 0,
            },
            new() {
                Key = "ftr-2025-010",
                FisNo = "FTR-2025-010", BelgeNo = "BLG-010",
                Tarih = new DateTime(2025,2,25), Saat = "11:00",
                TuruTxt = "Alış Faturası",
                CariKartKodu = "120.010", CariUnvan = "TOGG Finans A.Ş.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Genel Müdürlük", SubeKodu = "GM", SourceDepoAdi = "GM Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 95000m, ToplamKdv = 17100m, Net = 112100m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 3,
            },
            new() {
                Key = "ftr-2025-011",
                FisNo = "FTR-2025-011", BelgeNo = "BLG-011",
                Tarih = new DateTime(2025,3,5), Saat = "14:15",
                TuruTxt = "Hizmet Faturası",
                CariKartKodu = "120.011", CariUnvan = "ISS Facility Services",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Üretim Tesisi", SubeKodu = "UR", SourceDepoAdi = "Üretim Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 23400m, ToplamKdv = 4212m, Net = 27612m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Kismi, BekleyenKalemSayisi = 1,
            },
            new() {
                Key = "ftr-2025-012",
                FisNo = "FTR-2025-012", BelgeNo = "BLG-012",
                Tarih = new DateTime(2025,3,12), Saat = "09:30",
                TuruTxt = "Lisans Faturası",
                CariKartKodu = "120.012", CariUnvan = "Microsoft Türkiye",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Merkez", SubeKodu = "MK", SourceDepoAdi = "Ana Depo",
                DovizAdi = "USD", DovizKuru = 32.50m,
                Toplam = 38500m, ToplamKdv = 6930m, Net = 45430m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 2,
            },
            new() {
                Key = "ftr-2025-013",
                FisNo = "FTR-2025-013", BelgeNo = "BLG-013",
                Tarih = new DateTime(2025,3,18), Saat = "16:00",
                TuruTxt = "İletişim Faturası",
                CariKartKodu = "120.013", CariUnvan = "Sernet İletişim A.Ş.",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Anadolu Deposu", SubeKodu = "AN", SourceDepoAdi = "Anadolu Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 4200m, ToplamKdv = 756m, Net = 4956m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 2,
            },
            new() {
                Key = "ftr-2025-014",
                FisNo = "FTR-2025-014", BelgeNo = "BLG-014",
                Tarih = new DateTime(2025,3,22), Saat = "10:00",
                TuruTxt = "Sigorta Faturası",
                CariKartKodu = "120.014", CariUnvan = "Anadolu Sigorta",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Genel Müdürlük", SubeKodu = "GM", SourceDepoAdi = "GM Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 78000m, ToplamKdv = 14040m, Net = 92040m,
                Iptal = false, Kilitli = false, Muhasebelesme = true,
                TransferStatus = TransferStatus.Aktarildi, BekleyenKalemSayisi = 0,
            },
            new() {
                Key = "ftr-2025-015",
                FisNo = "FTR-2025-015", BelgeNo = "BLG-015",
                Tarih = new DateTime(2025,3,28), Saat = "08:00",
                TuruTxt = "Kargo Faturası",
                CariKartKodu = "120.015", CariUnvan = "UPS Kargo Hizmetleri",
                FirmaAdi = "Havuz Firma A.Ş.",
                SourceSubeAdi = "Havaalanı Şb.", SubeKodu = "HV", SourceDepoAdi = "Havaalanı Depo",
                DovizAdi = "TRY", DovizKuru = 1m,
                Toplam = 9800m, ToplamKdv = 1764m, Net = 11564m,
                Iptal = false, Kilitli = false, Muhasebelesme = false,
                TransferStatus = TransferStatus.Bekliyor, BekleyenKalemSayisi = 2,
            },
        };


        // ══════════════════════════════════════════════════════════════════════
        // SEED — Fatura Kalemleri (scf_fatura_kalemi_liste_view)
        // Tüm alan adları gerçek DİA view kolonlarıdır
        // ══════════════════════════════════════════════════════════════════════

        private static Dictionary<string, List<InvoiceLineListRowDto>> SeedInvoiceLines() => new()
        {
            // ── FTR-2025-101 kalemleri ────────────────────────────────────────
            ["ftr-2025-101"] = new()
            {
                LineRow("kal-101-01", "ftr-2025-101", 1, "STK.001", "Endüstriyel Anakart",
                    "ADET", 10m, 15000m, 150000m, 20m, 30000m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-101-02", "ftr-2025-101", 2, "STK.002", "Güç Kaynağı 500W",
                    "ADET", 50m, 2000m, 100000m, 20m, 20000m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-102 kalemleri ────────────────────────────────────────
            ["ftr-2025-102"] = new()
            {
                LineRow("kal-102-01", "ftr-2025-102", 1, "SRV.102", "Uçak Bileti Hizmeti",
                    "ADET", 15m, 3000m, 45000m, 20m, 9000m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-103 kalemleri ────────────────────────────────────────
            ["ftr-2025-103"] = new()
            {
                LineRow("kal-103-01", "ftr-2025-103", 1, "STK.103", "Server Kabini",
                    "ADET", 1m, 8000m, 8000m, 20m, 1600m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-103-02", "ftr-2025-103", 2, "STK.104", "Patch Panel",
                    "ADET", 5m, 400m, 2000m, 20m, 400m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-103-03", "ftr-2025-103", 3, "STK.105", "Cat6 Kablo 305mt",
                    "RULO", 2m, 1000m, 2000m, 20m, 400m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-104 kalemleri ────────────────────────────────────────
            ["ftr-2025-104"] = new()
            {
                LineRow("kal-104-01", "ftr-2025-104", 1, "LOJ.001", "Uluslararası Taşıma",
                    "SEFER", 1m, 12000m, 12000m, 20m, 2400m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-104-02", "ftr-2025-104", 2, "LOJ.002", "Gümrükleme Hizmeti",
                    "İŞLEM", 1m, 6500m, 6500m, 20m, 1300m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-105 kalemleri ────────────────────────────────────────
            ["ftr-2025-105"] = new()
            {
                LineRow("kal-105-01", "ftr-2025-105", 1, "YZL.001", "ERP Lisans Yenileme",
                    "ADET", 1m, 50000m, 50000m, 20m, 10000m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-105-02", "ftr-2025-105", 2, "YZL.002", "Yıllık Bakım Anlaşması",
                    "AY", 12m, 1000m, 12000m, 20m, 2400m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-001 kalemleri ────────────────────────────────────────
            ["ftr-2025-001"] = new()
            {
                LineRow("kal-001-01", "ftr-2025-001", 1, "ELKT.001", "Elektrik Gideri",
                    "ADET", 100m, 60m, 6000m, 20m, 1200m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-001-02", "ftr-2025-001", 2, "DGZ.001",  "Doğalgaz Gideri",
                    "ADET", 50m,  80m, 4000m, 20m, 800m,  0m, TransferStatus.Bekliyor),
                LineRow("kal-001-03", "ftr-2025-001", 3, "SU.001",   "Su Gideri",
                    "M3",   30m,  83.33m, 2500m, 20m, 500m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-002 kalemleri (kısmi) ───────────────────────────────
            ["ftr-2025-002"] = new()
            {
                LineRowTransferred("kal-002-01", "ftr-2025-002", 1, "TMZ.001", "Temizlik Hizmet Bedeli",
                    "ADET", 1m, 15000m, 15000m, 20m, 3000m,
                    "firma-002", "Beta Holding A.Ş.", "sube-002-mk", "Merkez", "donem-002-2025", "2025 Yılı"),
                LineRowTransferred("kal-002-02", "ftr-2025-002", 2, "GVN.001", "Güvenlik Hizmet Bedeli",
                    "ADET", 1m, 12000m, 12000m, 20m, 2400m,
                    "firma-003", "Gamma Ticaret Ltd. Şti.", "sube-003-mk", "Merkez", "donem-003-2025", "2025 Yılı"),
                LineRow("kal-002-03", "ftr-2025-002", 3, "VER.001", "Veri İletişim Gideri",
                    "AY",   5m, 1550m, 7750m, 20m, 1550m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-003 kalemleri ────────────────────────────────────────
            ["ftr-2025-003"] = new()
            {
                LineRow("kal-003-01", "ftr-2025-003", 1, "BAK.001", "Bakım-Onarım Gideri",
                    "ADET", 3m, 1800m, 5400m, 18m, 972m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-003-02", "ftr-2025-003", 2, "YDK.001", "Yedek Parça",
                    "ADET", 10m, 280m, 2800m, 18m, 504m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-004 kalemleri (tamamen aktarılmış) ───────────────────
            ["ftr-2025-004"] = new()
            {
                LineRowTransferred("kal-004-01", "ftr-2025-004", 1, "YAK.001", "Yakıt Gideri",
                    "LT", 500m, 58m, 29000m, 18m, 5220m,
                    "firma-002", "Beta Holding A.Ş.", "sube-002-mk", "Merkez", "donem-002-2025", "2025 Yılı"),
            },

            // ── FTR-2025-005 kalemleri ────────────────────────────────────────
            ["ftr-2025-005"] = new()
            {
                LineRow("kal-005-01", "ftr-2025-005", 1, "INT.001", "İnternet Hizmet Bedeli",
                    "AY", 1m, 3200m, 3200m, 18m, 576m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-005-02", "ftr-2025-005", 2, "TEL.001", "Telefon Hizmet Bedeli",
                    "AY", 1m, 2400m, 2400m, 18m, 432m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-006 kalemleri (kısmi) ───────────────────────────────
            ["ftr-2025-006"] = new()
            {
                LineRowTransferred("kal-006-01", "ftr-2025-006", 1, "YEM.001", "Yemek Hizmet Bedeli",
                    "KİŞİ", 300m, 30m, 9000m, 18m, 1620m,
                    "firma-003", "Gamma Ticaret Ltd. Şti.", "sube-003-ist", "İstanbul Şb.", "donem-003-2025", "2025 Yılı"),
                LineRow("kal-006-02", "ftr-2025-006", 2, "SRV.001", "Servis Hizmet Bedeli",
                    "AY", 100m, 55m, 5500m, 18m, 990m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-006-03", "ftr-2025-006", 3, "CAM.001", "Çamaşırhane Hizmeti",
                    "KG",  50m,  88m, 4400m, 18m, 792m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-007 kalemleri ────────────────────────────────────────
            ["ftr-2025-007"] = new()
            {
                LineRow("kal-007-01", "ftr-2025-007", 1, "ARC.001", "Araç Kiralama Bedeli",
                    "ADET", 6m, 4000m, 24000m, 20m, 4800m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-007-02", "ftr-2025-007", 2, "SGT.001", "Sigorta Gideri",
                    "ADET", 1m, 11000m, 11000m, 20m, 2200m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-007-03", "ftr-2025-007", 3, "BAK.002", "Araç Bakım Gideri",
                    "ADET", 1m, 7000m, 7000m, 20m, 1400m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-008 kalemleri ────────────────────────────────────────
            ["ftr-2025-008"] = new()
            {
                LineRow("kal-008-01", "ftr-2025-008", 1, "KLM.001", "Klima Bakım Hizmeti",
                    "ADET", 5m, 700m, 3500m, 18m, 630m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-008-02", "ftr-2025-008", 2, "UPS.001", "UPS Bakım Hizmeti",
                    "ADET", 3m, 1083.33m, 3250m, 18m, 585m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-009 kalemleri (tamamen aktarılmış) ───────────────────
            ["ftr-2025-009"] = new()
            {
                LineRowTransferred("kal-009-01", "ftr-2025-009", 1, "GVN.002", "Güvenlik Personel Gideri",
                    "ADET", 1m, 16500m, 16500m, 18m, 2970m,
                    "firma-002", "Beta Holding A.Ş.", "sube-002-mk", "Merkez", "donem-002-2025", "2025 Yılı"),
            },

            // ── FTR-2025-010 kalemleri ────────────────────────────────────────
            ["ftr-2025-010"] = new()
            {
                LineRow("kal-010-01", "ftr-2025-010", 1, "ARV.001", "Araç Satın Alma Peşinat",
                    "ADET", 1m, 60000m, 60000m, 20m, 12000m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-010-02", "ftr-2025-010", 2, "AKS.001", "Aksesuar Paketi",
                    "TAKIM", 1m, 15000m, 15000m, 20m, 3000m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-010-03", "ftr-2025-010", 3, "SRJ.001", "Şarj Ekipmanı",
                    "ADET", 2m, 10000m, 20000m, 20m, 4000m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-011 kalemleri (kısmi) ───────────────────────────────
            ["ftr-2025-011"] = new()
            {
                LineRowTransferred("kal-011-01", "ftr-2025-011", 1, "BIN.001", "Bina Temizlik Hizmeti",
                    "AY", 1m, 12000m, 12000m, 18m, 2160m,
                    "firma-002", "Beta Holding A.Ş.", "sube-002-an", "Anadolu", "donem-002-2025", "2025 Yılı"),
                LineRow("kal-011-02", "ftr-2025-011", 2, "PYZ.001", "Peyzaj Bakım Hizmeti",
                    "AY", 1m, 11400m, 11400m, 18m, 2052m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-012 kalemleri ────────────────────────────────────────
            ["ftr-2025-012"] = new()
            {
                LineRow("kal-012-01", "ftr-2025-012", 1, "LNS.001", "Office 365 Lisansı",
                    "KULLANICI", 50m, 440m, 22000m, 18m, 3960m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-012-02", "ftr-2025-012", 2, "BLT.001", "Azure Bulut Hizmeti",
                    "AY", 1m, 16500m, 16500m, 18m, 2970m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-013 kalemleri ────────────────────────────────────────
            ["ftr-2025-013"] = new()
            {
                LineRow("kal-013-01", "ftr-2025-013", 1, "HAT.001", "Hat Kurulum Bedeli",
                    "ADET", 2m, 1400m, 2800m, 18m, 504m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-013-02", "ftr-2025-013", 2, "DST.001", "Teknik Destek Bedeli",
                    "SAAT", 1m, 1400m, 1400m, 18m, 252m, 0m, TransferStatus.Bekliyor),
            },

            // ── FTR-2025-014 kalemleri (tamamen aktarılmış) ───────────────────
            ["ftr-2025-014"] = new()
            {
                LineRowTransferred("kal-014-01", "ftr-2025-014", 1, "ISY.001", "İşyeri Sigortası",
                    "ADET", 1m, 45000m, 45000m, 18m, 8100m,
                    "firma-003", "Gamma Ticaret Ltd. Şti.", "sube-003-mk", "Merkez", "donem-003-2025", "2025 Yılı"),
                LineRowTransferred("kal-014-02", "ftr-2025-014", 2, "SRM.001", "Sorumluluk Sigortası",
                    "ADET", 1m, 33000m, 33000m, 18m, 5940m,
                    "firma-003", "Gamma Ticaret Ltd. Şti.", "sube-003-mk", "Merkez", "donem-003-2025", "2025 Yılı"),
            },

            // ── FTR-2025-015 kalemleri ────────────────────────────────────────
            ["ftr-2025-015"] = new()
            {
                LineRow("kal-015-01", "ftr-2025-015", 1, "KRG.001", "Kargo Taşıma Gideri",
                    "KG", 200m, 30m, 6000m, 18m, 1080m, 0m, TransferStatus.Bekliyor),
                LineRow("kal-015-02", "ftr-2025-015", 2, "DEP.001", "Depo Kiralama Gideri",
                    "AY", 1m, 3800m, 3800m, 18m, 684m, 0m, TransferStatus.Bekliyor),
            },
        };

        // ══════════════════════════════════════════════════════════════════════
        // Helper — bekleyen kalem (uygulama özel alan değil, view alanı)
        // ══════════════════════════════════════════════════════════════════════

        private static InvoiceLineListRowDto LineRow(
            string key, string faturaKey, int sira,
            string stokKod, string stokAciklama,
            string birim, decimal miktar, decimal birimFiyati, decimal tutari,
            decimal kdv, decimal kdvTutari, decimal kdvTevkifatOrani,
            TransferStatus status)
        {
            return new InvoiceLineListRowDto
            {
                Key = key,
                FaturaKey = faturaKey,
                SiraNo = sira,
                StokHizmetKodu = stokKod,
                StokHizmetAciklama = stokAciklama,
                BirimKodu = birim,
                AnabirimKodu = birim,
                Miktar = miktar,
                AnaMiktar = miktar,
                BirimFiyati = birimFiyati,
                SonBirimFiyati = birimFiyati,
                YerelBirimFiyati = birimFiyati,
                Tutari = tutari,
                TutariSatirDovizi = tutari,
                DovizKuru = 1m,
                DovizAdi = "TRY",
                Kdv = kdv,
                KdvTutari = kdvTutari,
                KdvDurumu = 1,
                KdvTevkifatOrani = kdvTevkifatOrani,
                KdvTevkifatTutari = 0m,
                IndirimToplam = 0m,
                IndirimTutari = 0m,
                // Uygulama özel
                TransferStatus = status,
                IsSelected = false,
                DuplicateRisk = DuplicateRiskLevel.Yok,
                MappingStatus = MappingStatus.Eslenmedi,
            };
        }

        private static InvoiceLineListRowDto LineRowTransferred(
            string key, string faturaKey, int sira,
            string stokKod, string stokAciklama,
            string birim, decimal miktar, decimal birimFiyati, decimal tutari,
            decimal kdv, decimal kdvTutari,
            string tFirmaKey, string tFirmaKodu, string tSubeKey, string tSubeKodu,
            string tDonemKey, string tDonemKodu)
        {
            var row = LineRow(key, faturaKey, sira, stokKod, stokAciklama,
                              birim, miktar, birimFiyati, tutari, kdv, kdvTutari, 0m,
                              TransferStatus.Aktarildi);
            // Uygulama özel hedef bilgileri
            row.TargetFirmaKey  = tFirmaKey;
            row.TargetFirmaKodu = tFirmaKodu;
            row.TargetSubeKey   = tSubeKey;
            row.TargetSubeKodu  = tSubeKodu;
            row.TargetDonemKey  = tDonemKey;
            row.TargetDonemKodu = tDonemKodu;
            row.MappingStatus   = MappingStatus.Tamam;
            return row;
        }
    }
}
