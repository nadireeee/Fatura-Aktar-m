using DiaErpIntegration.API.Models;
using DiaErpIntegration.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiaErpIntegration.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class InvoiceController : ControllerBase
    {
        private readonly TransferService _transferService;

        public InvoiceController(TransferService transferService)
        {
            _transferService = transferService;
        }

        [HttpPost("transfer/execute")]
        public IActionResult ExecuteTransfer([FromBody] TransferRequestDto req)
        {
            return BadRequest("Aktarım ikinci aşamada aktif edilecek. Bu sürüm sadece okuma (listele/getir) içindir.");
        }

        [HttpPost("transfer/duplicate-check")]
        public IActionResult DuplicateCheck([FromBody] DuplicateCheckRequest req)
        {
            return BadRequest("Aktarım ikinci aşamada aktif edilecek. Bu sürüm sadece okuma (listele/getir) içindir.");
        }
    }
}

 