-- =============================================================================
-- DİA Özel Rapor — Havuz fatura listesi (RPR000000004 ile eşleşen şablon)
-- =============================================================================
-- SNAPSHOT: sahte varsayılan yok. `f.*` JSON’da `date` vb. ile çakışabildiği için ayrıca
--   snapshot_iso_date, snapshot_invoice_type_num, snapshot_cari_kodu, snapshot_currency_kodu
--   sütunları (SELECT sonunda) normalize + buildHeader tarafından öncelikli okunur.
-- Tek fatura teşhisi: WHERE sonuna geçici `AND f._key = 179413` ekleyin.
-- Toplu kalite: err_turu / err_cari / err_date / err_doviz + tek kolon snapshot_error (TURU CARI …).
-- =============================================================================
-- Proje (POST api/fatura-getir → rpr_raporsonuc_getir) Param sözlüğü:
--   firma_kodu, donem_kodu, baslangic, bitis, fatura_tipi, kaynak_sube, kaynak_depo,
--   ust_islem, cari_adi, fatura_no, fatura_turu, kalem_sube
-- DİA özel rapor entegrasyonu (çoğu şablonda):
--   {secilifirma}  → scf_fatura._level1  (seçili firma; API’de firma_kodu ile aynı değer)
--   {secilidonem}  → scf_fatura._level2  (seçili dönem; API’de donem_kodu ile aynı değer)
-- Backend (FaturaRaporController) hem {firma_kodu}/{donem_kodu} hem {secilifirma}/{secilidonem}
-- yer tutucularını aynı sayılarla doldurur; WHERE’de hangi isim kullanılırsa kullanılsın eşleşir.
-- Üst işlem filtresi: ust_islem 'TUM' | gerçek üst işlem kodu — tablo u.kodu ile eşlenir.
-- =============================================================================

SELECT
    f._key AS fatura_key,
    f.*,
    fk._key AS kalem_key,
    fk.*,
    COALESCE(
        NULLIF(f._key_sis_sube_source, 0),
        NULLIF(f._key_sis_sube_dest, 0),
        NULLIF(f._key_sis_sube, 0)
    ) AS kaynak_sube,
    NULLIF(BTRIM(COALESCE(ss_rpr.adi, ss_rpr.kodu, '')), '') AS kaynak_sube_adi,
    fk.__dinamik__fatsube AS fatsube,
    u.kodu AS ust_islem_kodu,
    u.aciklama AS ust_islem_aciklama,
    c.unvan AS cari_adi,
    -- Snapshot (frontend/backend IsValidSnapshot — camelCase JSON kolonları)
    TO_CHAR(f.tarih, 'YYYY-MM-DD') AS date,
    NULLIF(f.turu, 0) AS invoiceTypeCode,
    NULLIF(BTRIM(COALESCE(c.kodu, f.carikartkodu, '')), '') AS cariCode,
    NULLIF(BTRIM(COALESCE(c.kodu, f.carikartkodu, '')), '') AS carikartkodu,
    NULLIF(
        BTRIM(
            COALESCE(
                NULLIF(TRIM(dv.kodu), ''),
                NULLIF(TRIM(dv.dovizkodu), ''),
                NULLIF(TRIM(dv.adi), ''),
                NULLIF(TRIM(f.dovizkodu), '')
            )
        ),
        ''
    ) AS currencyCode,
    fk.kalemturu,
    f._key_sis_doviz AS fatura_doviz_key,
    f.dovizkuru AS fatura_doviz_kur,
    fk.kampanyakodu AS kampanya_kodu,
    fk._key_sis_doviz AS kalem_doviz_key,
    fk.dovizkuru AS kalem_doviz_kur,
    fk._key_scf_odeme_plani AS odeme_plani_key,
    fk._key_scf_promosyon AS promosyon_key,

CASE
    WHEN fk.kalemturu = 'MLZM' THEN 'STOK'
    WHEN fk.kalemturu = 'HZMT' THEN 'HIZMET'
ELSE fk.kalemturu
END AS kalem_tipi,

    COALESCE(s.stokkartkodu, h.hizmetkartkodu) AS stok_hizmet_kodu,
    COALESCE(s.aciklama, h.aciklama, '') AS stok_hizmet_adi,
    COALESCE(fk.birimkodu, s.anabirimadi, h.birimadi, '') AS birim_adi,

    -- Aktarım snapshot (JSON’da f.* / fk.* ile çakışmasın diye benzersiz adlar — SON sütunlar)
    TO_CHAR(f.tarih, 'YYYY-MM-DD') AS snapshot_iso_date,
    NULLIF(f.turu, 0) AS snapshot_invoice_type_num,
    NULLIF(BTRIM(COALESCE(c.kodu, f.carikartkodu, '')), '') AS snapshot_cari_kodu,
    NULLIF(
        BTRIM(
            COALESCE(
                NULLIF(TRIM(dv.kodu), ''),
                NULLIF(TRIM(dv.dovizkodu), ''),
                NULLIF(TRIM(dv.adi), ''),
                NULLIF(TRIM(f.dovizkodu), '')
            )
        ),
        ''
    ) AS snapshot_currency_kodu,

    -- Toplu veri kalitesi (hangi faturada ne eksik — tek bakışta)
    CASE WHEN f.turu IS NULL OR f.turu = 0 THEN 'ERR_TURU' END AS err_turu,
    CASE
        WHEN NULLIF(BTRIM(COALESCE(c.kodu::text, f.carikartkodu::text, '')), '') IS NULL THEN 'ERR_CARI'
    END AS err_cari,
    CASE WHEN f.tarih IS NULL THEN 'ERR_DATE' END AS err_date,
    CASE
        WHEN NULLIF(
            BTRIM(
                COALESCE(
                    NULLIF(TRIM(dv.kodu), ''),
                    NULLIF(TRIM(dv.dovizkodu), ''),
                    NULLIF(TRIM(dv.adi), ''),
                    NULLIF(TRIM(f.dovizkodu), '')
                )
            ),
            ''
        ) IS NULL
        THEN 'ERR_DOVIZ'
    END AS err_doviz,
    NULLIF(
        TRIM(
            CONCAT(
                CASE WHEN f.turu IS NULL OR f.turu = 0 THEN 'TURU ' ELSE '' END,
                CASE
                    WHEN NULLIF(BTRIM(COALESCE(c.kodu::text, f.carikartkodu::text, '')), '') IS NULL THEN 'CARI '
                    ELSE ''
                END,
                CASE WHEN f.tarih IS NULL THEN 'DATE ' ELSE '' END,
                CASE
                    WHEN NULLIF(
                        BTRIM(
                            COALESCE(
                                NULLIF(TRIM(dv.kodu), ''),
                                NULLIF(TRIM(dv.dovizkodu), ''),
                                NULLIF(TRIM(dv.adi), ''),
                                NULLIF(TRIM(f.dovizkodu), '')
                            )
                        ),
                        ''
                    ) IS NULL
                    THEN 'DOVIZ '
                    ELSE ''
                END
            )
        ),
        ''
    ) AS snapshot_error

FROM scf_fatura f

INNER JOIN scf_fatura_kalemi_liste_view fk
    ON fk._key_scf_fatura = f._key

LEFT JOIN sis_ust_islem_turu u
    ON u._key = f._key_sis_ust_islem_turu

LEFT JOIN scf_carikart_liste_view c
    ON c._key = f._key_scf_carikart

-- DİA şema farkı varsa: görünüm adı veya kolonlar (kodu/adi) tenant’a göre düzeltilir.
LEFT JOIN sis_doviz_liste_view dv
    ON dv._key = f._key_sis_doviz

-- Kaynak şube adı (normalize kaynak_sube_adi / şube key — UI "Bilinmiyor" önleme)
-- Tenant'ta görünüm adı farklıysa (sis_sube / sis_sube_view) LEFT JOIN hedefini düzeltin.
LEFT JOIN sis_sube_liste_view ss_rpr
    ON ss_rpr._key = COALESCE(
        NULLIF(f._key_sis_sube_source, 0),
        NULLIF(f._key_sis_sube_dest, 0),
        NULLIF(f._key_sis_sube, 0)
    )

LEFT JOIN scf_stokkart_liste_view s
    ON fk.kalemturu = 'MLZM'
   AND s._key = fk._key_kalemturu

LEFT JOIN scf_hizmetkart_liste_view h
    ON fk.kalemturu = 'HZMT'
   AND h._key = fk._key_kalemturu

WHERE
    f._level1 = {secilifirma}
    AND f._level2 = {secilidonem}
    AND f.tarih BETWEEN '{baslangic}' AND '{bitis}'

AND (
  COALESCE(NULLIF(TRIM('{fatura_tipi}'), ''), 'ALL') = 'ALL'
  OR (
    TRIM('{fatura_tipi}') = 'TUM'
    AND (
      fk.__dinamik__fatsube IS NULL
      OR TRIM(fk.__dinamik__fatsube) = ''
    )
  )
  OR (
    TRIM('{fatura_tipi}') = 'DAGIT'
    AND (
      fk.__dinamik__fatsube IS NOT NULL
      AND TRIM(fk.__dinamik__fatsube) <> ''
    )
  )
)

AND (
    {kaynak_sube} = 0
    OR f._key_sis_sube_source = {kaynak_sube}
    OR f._key_sis_sube_dest = {kaynak_sube}
)

AND (
    {kaynak_depo} = 0
    OR fk._key_sis_depo_source = {kaynak_depo}
    OR fk._key_sis_depo_dest = {kaynak_depo}
)

AND (
    '{ust_islem}' = 'TUM'
    OR u.kodu = '{ust_islem}'
)

AND (
    COALESCE(TRIM('{cari_adi}'), '') = ''
    OR c.unvan ILIKE '%' || TRIM('{cari_adi}') || '%'
)

AND (
    COALESCE(TRIM('{fatura_no}'), '') = ''
    OR f.belgeno2 ILIKE '%' || TRIM('{fatura_no}') || '%'
)

AND (
    NULLIF(TRIM('{fatura_turu}'), '') IS NULL
    OR f.turu = CAST(NULLIF(TRIM('{fatura_turu}'), '') AS INTEGER)
)

AND (
    COALESCE(TRIM('{kalem_sube}'), '') = ''
    OR fk.__dinamik__fatsube ILIKE '%' || TRIM('{kalem_sube}') || '%'
);
