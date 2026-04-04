using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HandlebarsDotNet;
using Jellyfin.Plugin.Streamyfin.Configuration;
using Jellyfin.Plugin.Streamyfin.Extensions;
using Jellyfin.Plugin.Streamyfin.PushNotifications;
using Jellyfin.Plugin.Streamyfin.PushNotifications.models;
using Jellyfin.Plugin.Streamyfin.Storage.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Streamyfin.Api;

public class JsonStringResult : ContentResult
{
  public JsonStringResult(string json)
  {
    Content = json;
    ContentType = "application/json";
  }
}

public class ConfigYamlRes
{
  public string Value { get; set; } = default!;
}

public class ConfigSaveResponse
{
  public bool Error { get; set; }
  public string Message { get; set; } = default!;
}

public class SeerrConfigureRequest
{
  public string SeerrUrl { get; set; } = default!;
  public string SeerrApiKey { get; set; } = default!;
  public string? JellyfinPublicUrl { get; set; }
}

public class SeerrConfigureResponse
{
  public bool Success { get; set; }
  public string? Message { get; set; }
  public string? WebhookId { get; set; }
}

public class SeerrStatusResponse
{
  public bool Connected { get; set; }
  public string? SeerrUrl { get; set; }
  public string? WebhookId { get; set; }
}

public class BroadcastRequest
{
  public string Title { get; set; } = default!;
  public string Body { get; set; } = default!;
}

public class BroadcastResponse
{
  public int SentCount { get; set; }
  public int FailedCount { get; set; }
}

//public class ConfigYamlReq {
//  public string? Value { get; set; }
//}

/// <summary>
/// CollectionImportController.
/// </summary>
[ApiController]
[Route("streamyfin")]
public class StreamyfinController : ControllerBase
{
  private readonly ILogger<StreamyfinController> _logger;
  private readonly ILoggerFactory _loggerFactory;
  private readonly IServerConfigurationManager _config;
  private readonly IUserManager _userManager;
  private readonly ILibraryManager _libraryManager;
  private readonly IDtoService _dtoService;
  private readonly SerializationHelper _serializationHelperService;
  private readonly NotificationHelper _notificationHelper;
  private readonly IHttpClientFactory _httpClientFactory;

  public StreamyfinController(
    ILoggerFactory loggerFactory,
    IDtoService dtoService,
    IServerConfigurationManager config,
    IUserManager userManager,
    ILibraryManager libraryManager,
    SerializationHelper serializationHelper,
    NotificationHelper notificationHelper,
    IHttpClientFactory httpClientFactory
  )
  {
    _loggerFactory = loggerFactory;
    _logger = loggerFactory.CreateLogger<StreamyfinController>();
    _dtoService = dtoService;
    _config = config;
    _userManager = userManager;
    _libraryManager = libraryManager;
    _serializationHelperService = serializationHelper;
    _notificationHelper = notificationHelper;
    _httpClientFactory = httpClientFactory;

    _logger.LogInformation("StreamyfinController Loaded");
  }

  [HttpPost("config/yaml")]
  [Authorize(Policy = Policies.RequiresElevation)]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult<ConfigSaveResponse> saveConfig(
    [FromBody, Required] ConfigYamlRes config
  )
  {
    Config p;
    try
    {
      p = _serializationHelperService.Deserialize<Config>(config.Value);
    }
    catch (Exception e)
    {

      return new ConfigSaveResponse { Error = true, Message = e.ToString() };
    }

    var c = StreamyfinPlugin.Instance!.Configuration;
    c.Config = p;
    StreamyfinPlugin.Instance!.UpdateConfiguration(c);

    return new ConfigSaveResponse { Error = false };
  }

  [HttpGet("config")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult getConfig()
  {
    var config = StreamyfinPlugin.Instance!.Configuration.Config;
    return new JsonStringResult(_serializationHelperService.SerializeToJson(config));
  }

  [HttpGet("config/schema")]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult getConfigSchema(
  )
  {
    return new JsonStringResult(SerializationHelper.GetJsonSchema<Config>());
  }

  [HttpGet("config/yaml")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult<ConfigYamlRes> getConfigYaml()
  {
    return new ConfigYamlRes
    {
      Value = _serializationHelperService.SerializeToYaml(StreamyfinPlugin.Instance!.Configuration.Config)
    };
  }
  
  [HttpGet("config/default")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult<ConfigYamlRes> getDefaultConfig()
  {
    return new ConfigYamlRes
    {
      Value = _serializationHelperService.SerializeToYaml(PluginConfiguration.DefaultConfig())
    };
  }

  /// <summary>
  /// Post expo push tokens for a specific user & device 
  /// </summary>
  /// <param name="deviceToken"></param>
  [HttpPost("device")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult PostDeviceToken([FromBody, Required] DeviceToken deviceToken)
  {
    _logger.LogInformation("Posting device token for deviceId: {0}", deviceToken.DeviceId);
    return new JsonResult(
      _serializationHelperService.ToJson(StreamyfinPlugin.Instance!.Database.AddDeviceToken(deviceToken))
    );
  }
  
  /// <summary>
  /// Delete expo push tokens for a specific device 
  /// </summary>
  /// <param name="deviceId"></param>
  [HttpDelete("device/{deviceId}")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult DeleteDeviceToken([FromRoute, Required] Guid? deviceId)
  {
    if (deviceId == null) return BadRequest("Device id is required");

    _logger.LogInformation("Deleting device token for deviceId: {0}", deviceId);
    StreamyfinPlugin.Instance!.Database.RemoveDeviceToken((Guid) deviceId);

    return new OkResult();
  }

  /// <summary>
  /// Forward notifications to expos push service using persisted device tokens
  /// </summary>
  /// <param name="notifications"></param>
  /// <returns></returns>
  [HttpPost("notification")]
  [Authorize]
  [ProducesResponseType(StatusCodes.Status200OK)]
  [ProducesResponseType(StatusCodes.Status202Accepted)]
  public ActionResult PostNotifications([FromBody, Required] List<Notification> notifications)
  {
    var db = StreamyfinPlugin.Instance?.Database;

    if (db?.TotalDevicesCount() == 0)
    {
      _logger.LogInformation("There are currently no devices setup to receive push notifications");
      return new AcceptedResult();
    }

    List<DeviceToken>? allTokens = null;
    var validNotifications = notifications
      .FindAll(n =>
      {
        var title = n.Title ?? "";
        var body = n.Body ?? "";
        
        // Title and body are both valid
        if (!title.IsNullOrNonWord() && !body.IsNullOrNonWord())
        {
          return true;
        }

        // Title can be empty, body is required.
        return string.IsNullOrEmpty(title) && !body.IsNullOrNonWord();
        // every other scenario is invalid
      })
      .Select(notification =>
      {
        List<DeviceToken> tokens = [];
        var expoNotification = notification.ToExpoNotification();
        
        // Get tokens for target user
        if (notification.UserId != null || !string.IsNullOrWhiteSpace(notification.Username))
        {
          Guid? userId = null;

          if (notification.UserId != null)
          {
            userId = notification.UserId;
          } 
          else if (notification.Username != null)
          {
            userId = _userManager.Users.ToList().Find(u => u.Username == notification.Username)?.Id;
          }
          if (userId != null)
          {
            _logger.LogInformation("Getting device tokens associated to userId: {0}", userId);
            tokens.AddRange(
              db?.GetUserDeviceTokens((Guid) userId)
              ?? []
            );
          }
        }
        // Get all available tokens
        else if (!notification.IsAdmin)
        {
          _logger.LogInformation("No user target provided. Getting all device tokens...");
          allTokens ??= db?.GetAllDeviceTokens() ?? [];
          tokens.AddRange(allTokens);
          _logger.LogInformation("All known device tokens count: {0}", allTokens.Count);
        }

        // Get all available tokens for admins
        if (notification.IsAdmin)
        {
          _logger.LogInformation("Notification being posted for admins");
          tokens.AddRange(_userManager.GetAdminDeviceTokens());
        }

        expoNotification.To = tokens.Select(t => t.Token).Distinct().ToList();

        return expoNotification;
      })
      .Where(n => n.To.Count > 0)
      .ToArray();

    _logger.LogInformation("Received {0} valid notifications", validNotifications.Length);

    if (validNotifications.Length == 0)
    {
      return new AcceptedResult();
    }

    _logger.LogInformation("Posting notifications...");
    var task = _notificationHelper.Send(validNotifications);
    task.Wait();
    return new JsonResult(_serializationHelperService.ToJson(task.Result));
  }

  [HttpGet("seerr/status")]
  [Authorize(Policy = Policies.RequiresElevation)]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public ActionResult<SeerrStatusResponse> GetSeerrStatus()
  {
    var cfg = StreamyfinPlugin.Instance!.Configuration;
    return new SeerrStatusResponse
    {
      Connected = cfg.SeerrWebhookEnabled,
      SeerrUrl = cfg.SeerrUrl,
      WebhookId = cfg.SeerrWebhookId
    };
  }

  [HttpPost("seerr/configure")]
  [Authorize(Policy = Policies.RequiresElevation)]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public async Task<ActionResult<SeerrConfigureResponse>> ConfigureSeerr(
    [FromBody, Required] SeerrConfigureRequest request)
  {
    var baseUrl = string.IsNullOrWhiteSpace(request.JellyfinPublicUrl)
      ? $"{Request.Scheme}://{Request.Host}"
      : request.JellyfinPublicUrl.TrimEnd('/');
    var webhookTarget = $"{baseUrl}/streamyfin/notification";

    var jsonPayload = JsonSerializer.Serialize(new[]
    {
      new { title = "{{event}}", body = "{{subject}}: {{message}}", isAdmin = true }
    });

    var payload = new
    {
      enabled = true,
      types = 2046,
      options = new { webhookUrl = webhookTarget, jsonPayload, authHeader = (string?)null }
    };

    var cfg = StreamyfinPlugin.Instance!.Configuration;
    var existingId = cfg.SeerrWebhookId;
    var seerrBase = request.SeerrUrl.TrimEnd('/');
    var endpoint = string.IsNullOrEmpty(existingId)
      ? $"{seerrBase}/api/v1/settings/notifications/webhook"
      : $"{seerrBase}/api/v1/settings/notifications/webhook/{existingId}";
    var method = string.IsNullOrEmpty(existingId) ? HttpMethod.Post : HttpMethod.Put;

    var client = _httpClientFactory.CreateClient();
    using var httpRequest = new HttpRequestMessage(method, endpoint);
    httpRequest.Headers.Add("X-Api-Key", request.SeerrApiKey);
    httpRequest.Content = JsonContent.Create(payload);

    HttpResponseMessage response;
    try
    {
      response = await client.SendAsync(httpRequest).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to reach Seerr at {Url}", endpoint);
      return new SeerrConfigureResponse { Success = false, Message = ex.Message };
    }

    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new SeerrConfigureResponse
      {
        Success = false,
        Message = $"Seerr returned {(int)response.StatusCode}: {body}"
      };
    }

    string? webhookId = existingId;
    try
    {
      using var doc = await JsonDocument.ParseAsync(
        await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
      if (doc.RootElement.TryGetProperty("id", out var idEl))
        webhookId = idEl.GetRawText().Trim('"');
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Could not extract webhook id from Seerr response");
    }

    cfg.SeerrUrl = request.SeerrUrl;
    cfg.SeerrApiKey = request.SeerrApiKey;
    cfg.SeerrWebhookEnabled = true;
    cfg.SeerrWebhookId = webhookId;
    StreamyfinPlugin.Instance!.UpdateConfiguration(cfg);

    return new SeerrConfigureResponse { Success = true, WebhookId = webhookId };
  }

  [HttpPost("seerr/test")]
  [Authorize(Policy = Policies.RequiresElevation)]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public async Task<ActionResult<SeerrConfigureResponse>> TestSeerr()
  {
    var cfg = StreamyfinPlugin.Instance!.Configuration;
    if (string.IsNullOrEmpty(cfg.SeerrUrl) || string.IsNullOrEmpty(cfg.SeerrApiKey))
      return new SeerrConfigureResponse { Success = false, Message = "Seerr is not configured" };

    var client = _httpClientFactory.CreateClient();
    var testUrl = $"{cfg.SeerrUrl.TrimEnd('/')}/api/v1/settings/notifications/webhook/test";
    using var req = new HttpRequestMessage(HttpMethod.Post, testUrl);
    req.Headers.Add("X-Api-Key", cfg.SeerrApiKey);
    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

    try
    {
      var response = await client.SendAsync(req).ConfigureAwait(false);
      return response.IsSuccessStatusCode
        ? new SeerrConfigureResponse { Success = true }
        : new SeerrConfigureResponse
          {
            Success = false,
            Message = $"Test failed with status {(int)response.StatusCode}"
          };
    }
    catch (Exception ex)
    {
      return new SeerrConfigureResponse { Success = false, Message = ex.Message };
    }
  }

  /// <summary>
  /// Send a push notification to all registered devices (admin only).
  /// If a Handlebars template named "broadcast" exists at
  /// {dataPath}/streamyfin/templates/broadcast.hbs it is used to render the body;
  /// otherwise the raw title and body are used as-is.
  /// </summary>
  [HttpPost("broadcast")]
  [Authorize(Policy = Policies.RequiresElevation)]
  [ProducesResponseType(StatusCodes.Status200OK)]
  public async Task<ActionResult<BroadcastResponse>> Broadcast(
    [FromBody, Required] BroadcastRequest request)
  {
    _logger.LogInformation("Broadcast requested: title={0}", request.Title);

    var title = request.Title;
    var body = request.Body;

    var templatePath = Path.Combine(
      _config.ApplicationPaths.DataPath,
      "streamyfin",
      "templates",
      "broadcast.hbs");

    if (File.Exists(templatePath))
    {
      _logger.LogInformation("Applying broadcast Handlebars template from {0}", templatePath);
      try
      {
        var source = await File.ReadAllTextAsync(templatePath).ConfigureAwait(false);
        var template = Handlebars.Compile(source);
        body = template(new { title = request.Title, body = request.Body });
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to render broadcast Handlebars template, falling back to raw body");
      }
    }

    var notification = new ExpoNotificationRequest
    {
      Title = title,
      Body = body
    };

    _logger.LogInformation("Sending broadcast to all devices");
    var response = await _notificationHelper.SendToAll(notification).ConfigureAwait(false);

    if (response == null)
    {
      _logger.LogInformation("Broadcast complete: no registered devices found");
      return new BroadcastResponse { SentCount = 0, FailedCount = 0 };
    }

    var sentCount = response.Data?
      .Count(t => string.Equals(t.Status, "ok", StringComparison.OrdinalIgnoreCase)) ?? 0;
    var failedCount = response.Data?
      .Count(t => !string.Equals(t.Status, "ok", StringComparison.OrdinalIgnoreCase)) ?? 0;

    _logger.LogInformation("Broadcast complete: sent={0}, failed={1}", sentCount, failedCount);

    return new BroadcastResponse { SentCount = sentCount, FailedCount = failedCount };
  }
}
