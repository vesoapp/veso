using Microsoft.AspNetCore.Mvc;

namespace Veso.Api
{
    /// <summary>
    /// Base api controller for the API setting a default route.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class BaseVesoApiController : ControllerBase
    {
    }
}
