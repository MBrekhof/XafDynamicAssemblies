using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.Security.Authentication;
using DevExpress.ExpressApp.Security.Authentication.ClientServer;
using Microsoft.AspNetCore.Mvc;

namespace XafDynamicAssemblies.Blazor.Server.API.Security
{
    // SEC-004: POST /api/Authentication/Authenticate { "userName": "Admin", "password": "" }
    // returns a bearer token for the OData endpoints. Lifted from the DX 26.1 template.
    [ApiController]
    [Route("api/[controller]")]
    public class AuthenticationController : ControllerBase
    {
        readonly IAuthenticationTokenProvider tokenProvider;
        public AuthenticationController(IAuthenticationTokenProvider tokenProvider)
        {
            this.tokenProvider = tokenProvider;
        }
        [HttpPost("Authenticate")]
        public IActionResult Authenticate([FromBody] AuthenticationStandardLogonParameters logonParameters)
        {
            try
            {
                return Ok(tokenProvider.Authenticate(logonParameters));
            }
            catch (AuthenticationException ex)
            {
                return Unauthorized(ex.GetJson());
            }
        }
    }
}
