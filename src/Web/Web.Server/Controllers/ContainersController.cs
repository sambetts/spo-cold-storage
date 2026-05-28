using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// <c>GET /api/containers</c> – containers the calling user is allowed to see.
/// Enforces the container ACL rules so users never receive a name they cannot
/// at least browse.
/// </summary>
[Authorize]
[ApiController]
[Route("api/containers")]
public class ContainersController(IContainerAccessService containerAccess) : ControllerBase
{
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ContainerResponse>>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await _containerAccess.ListVisibleContainersAsync(User, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
