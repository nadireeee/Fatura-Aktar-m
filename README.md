# 📋 DİA ERP — Havuz Fatura Aktarım Modülü

Bu proje, DİA ERP sistemindeki "Havuz" firmada biriken faturaların, hedef firma, şube ve dönem bazlı olarak akıllı bir şekilde aktarılmasını sağlayan profesyonel bir entegrasyon modülüdür.

## 🚀 Öne Çıkan Özellikler

- **Gerçek DİA Mimari Entegrasyonu**: `scf_fatura_liste_view` ve `scf_fatura_kalemi_liste_view` yapılarıyla birebir uyumludur.
- **Kalem Bazlı Aktarım**: Faturanın tamamı yerine, sadece seçilen satırların hedef firmaya transfer edilmesine imkan tanır.
- **Akıllı Mükerrer Kontrolü**: `belgeno + tarih + stokkodu + miktar` kombinasyonu ile aynı kalemin aynı hedefe mükerrer aktarılmasını otomatik olarak engeller.
- **Dinamik Hedefleme**: DİA `sis_firma`, `sis_sube` ve `sis_donem` tabloları üzerinden anlık şube ve dönem seçimi sunar.
- **Gelişmiş Görselleştirme**: Aktarım durumları (Bekliyor, Kısmi, Aktarıldı) hem üst hem de kalem seviyesinde renkli badge'ler ile takip edilir.

## 🛠️ Teknoloji Yığını

- **Backend**: .NET 9 Web API
- **Frontend**: React (Vite) + TypeScript
- **Stil**: Vanilla CSS (Premium ERP Teması)
- **Mimari**: Standart DİA alanları ile uygulamaya özel (target) alanların net ayrımı.

## 📂 Proje Yapısı

- `/Backend`: .NET API, Mock Veri Deposu ve Aktarım Servisi.
- `/Frontend`: React TypeScript arayüzü ve API servis katmanı.

---
*Not: Bu uygulama DİA Web Servisleri ile entegre çalışacak şekilde tasarlanmıştır.*
