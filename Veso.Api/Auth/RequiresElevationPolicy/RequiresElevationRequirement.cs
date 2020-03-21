using Microsoft.AspNetCore.Authorization;

namespace veso.Api.Auth.RequiresElevationPolicy
{
    /// <summary>
    /// The authorization requirement for requiring elevated privileges in the authorization handler.
    /// </summary>
    public class RequiresElevationRequirement : IAuthorizationRequirement
    {
    }
}
