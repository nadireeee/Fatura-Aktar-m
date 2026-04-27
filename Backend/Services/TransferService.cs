using DiaErpIntegration.API.Models;
using Microsoft.Extensions.Logging;

namespace DiaErpIntegration.API.Services
{
    public class TransferService
    {
        // Bu sprint: sadece okuma (listele/getir). Aktarım ikinci aşamada.
        public TransferService(ILogger<TransferService> logger) { }

        public static string BuildCompositeKey(
            string sourceFaturaKey, string sourceKalemKey,
            string targetFirmaKodu, string targetSubeKodu, string targetDonemKodu)
            => $"{sourceFaturaKey}|{sourceKalemKey}|{targetFirmaKodu}|{targetSubeKodu}|{targetDonemKodu}";

        public Task<TransferResultDto> ExecuteTransferAsync(TransferRequestDto request)
            => Task.FromResult(new TransferResultDto { Success = false, Message = "Aktarım ikinci aşamada aktif edilecek." });

        public DuplicateCheckResult CheckDuplicate(DuplicateCheckRequest req)
        {
            var compositeKey = BuildCompositeKey(
                req.SourceFaturaKey, req.SourceKalemKey,
                req.TargetFirmaKey, req.TargetSubeKodu, req.TargetDonemKodu);

            return new DuplicateCheckResult
            {
                SourceKalemKey = req.SourceKalemKey,
                IsDuplicate = false,
                RiskLevel = DuplicateRiskLevel.Yok,
                Reason = null,
                CanOverride = true
            };
        }
    }
}


