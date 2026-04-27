using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DiaErpIntegration.API.Models
{
    // DİA WS Request/Response base models for v3
    public class DiaWsRequestBase
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;
        
        [JsonPropertyName("firma_kodu")]
        public int FirmaKodu { get; set; }
        
        [JsonPropertyName("donem_kodu")]
        public int DonemKodu { get; set; }
    }

    public class DiaGenericRequest<T> : DiaWsRequestBase
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    public class DiaListRequest : DiaWsRequestBase
    {
        [JsonPropertyName("filtre")]
        public string Filtre { get; set; } = string.Empty;
        
        [JsonPropertyName("sirala")]
        public string Sirala { get; set; } = string.Empty;
        
        [JsonPropertyName("limit")]
        public int Limit { get; set; } = 100;
        
        [JsonPropertyName("offset")]
        public int Offset { get; set; } = 0;
    }

    // Login Request (Special Case)
    public class DiaLoginRequest
    {
        [JsonPropertyName("login")]
        public DiaLoginData Login { get; set; } = new();
    }

    public class DiaLoginData
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;
        
        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("disconnect_same_user")]
        public string DisconnectSameUser { get; set; } = "True";

        // DİA WS v3: api key sadece login'de params.apikey ile gönderilir
        [JsonPropertyName("params")]
        public Dictionary<string, string> Params { get; set; } = new();
    }

    public class DiaLoginResponse
    {
        [JsonPropertyName("msg")]
        public string SessionId { get; set; } = string.Empty;
        
        [JsonPropertyName("code")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Code { get; set; } 
    }

    // Generic Response
    public class DiaWsResponseBase
    {
        [JsonPropertyName("code")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Code { get; set; }
        
        [JsonPropertyName("msg")]
        public string Message { get; set; } = string.Empty;
    }

    public class DiaWsResponse<T> : DiaWsResponseBase
    {
        [JsonPropertyName("result")]
        public T? Result { get; set; }
    }

    // Specific DİA Models
    public class DiaInvoiceAddRequest : DiaWsRequestBase
    {
        [JsonPropertyName("kart")]
        public DiaInvoiceModel Kart { get; set; } = new();
    }

    public class DiaInvoiceModel
    {
        [JsonPropertyName("_key")]
        public string? Key { get; set; }

        [JsonPropertyName("fisno")]
        public string FisNo { get; set; } = string.Empty;

        [JsonPropertyName("tarih")]
        public string Tarih { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");

        [JsonPropertyName("subekodu")]
        public string SubeKodu { get; set; } = string.Empty;

        [JsonPropertyName("depokodu")]
        public string DepoKodu { get; set; } = string.Empty;

        [JsonPropertyName("aciklama1")]
        public string Aciklama1 { get; set; } = "Pool transfer";

        [JsonPropertyName("kalemler")]
        public List<DiaInvoiceLineModel> Kalemler { get; set; } = new();
    }

    public class DiaInvoiceLineModel
    {
        [JsonPropertyName("_key")]
        public string? Key { get; set; }

        [JsonPropertyName("stokhizmetkodu")]
        public string StokHizmetKodu { get; set; } = string.Empty;
        
        [JsonPropertyName("miktar")]
        public decimal Miktar { get; set; }
        
        [JsonPropertyName("birimfiyat")]
        public decimal BirimFiyat { get; set; }

        [JsonPropertyName("tutar")]
        public decimal Tutar { get; set; }
    }
}

