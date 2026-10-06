using System.Security.Claims;
using Downloader.Data.DTOs;
using Downloader.DataApi.Services;
using Downloader.WebApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Downloader.WebApi.Controllers;

/// <summary>
/// API 密钥管理。只认 JWT 登录的 Admin，
/// 密钥自身不能调用这里，避免一把泄露的密钥无限自我续期。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.JwtAdmin)]
public class ApiKeyController(IApiKeyService apiKeyService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ApiKeyDto>>> GetAll()
    {
        var apiKeys = await apiKeyService.GetAllAsync();
        return Ok(apiKeys);
    }

    /// <summary>
    /// 创建密钥。返回体里的 Key 是明文，只会出现这一次，请立即保存。
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiKeyCreatedDto>> Create([FromBody] ApiKeyCreateDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var apiKey = await apiKeyService.CreateAsync(dto, userId);
        if (apiKey == null) return BadRequest("当前登录用户不存在，无法创建密钥。");
        return Ok(apiKey);
    }

    /// <summary>
    /// 启用或停用密钥
    /// </summary>
    [HttpPut("{id}/status")]
    public async Task<ActionResult> SetStatus(string id, [FromBody] ApiKeyStatusDto dto)
    {
        var success = await apiKeyService.SetActiveAsync(id, dto.IsActive);
        if (!success) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id)
    {
        var success = await apiKeyService.DeleteAsync(id);
        if (!success) return NotFound();
        return NoContent();
    }
}
