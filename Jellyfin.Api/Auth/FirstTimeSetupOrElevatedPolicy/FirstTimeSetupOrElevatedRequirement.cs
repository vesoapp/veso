using Microsoft.AspNetCore.Authorization;

namespace Veso.Api.Auth.FirstTimeSetupOrElevatedPolicy
{
    /// <summary>
    /// The authorization requirement, requiring incomplete first time setup or elevated privileges, for the authorization handler.
    /// </summary>
    public class FirstTimeSetupOrElevatedRequirement : IAuthorizationRequirement
    {
    }
}
