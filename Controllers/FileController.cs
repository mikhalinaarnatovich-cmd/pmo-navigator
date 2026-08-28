using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using PmoNav.Services;

namespace PmoNav.Controllers;

[ApiController]
[Route("api/file")]
public class FileController : ControllerBase
{
    private readonly string _projectsBasePath;
    private readonly ICurrentUserService _user;

    // Расширения, которые браузер может показать сам, без скачивания копии.
    private static readonly HashSet<string> InlinePreviewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".txt", ".csv", ".mp4", ".webm"
    };

    public FileController(IConfiguration config, ICurrentUserService user)
    {
        _projectsBasePath = config["ProjectsBasePath"] ?? @"Z:\Документы\Common\РМО";
        // Тестовый режим: если сетевой диск недоступен (например, на Linux-хостинге),
        // используем демо-документы из репозитория wwwroot/test-docs
        if (!Directory.Exists(_projectsBasePath))
        {
            var local = Path.Combine(AppContext.BaseDirectory, "wwwroot", "test-docs");
            if (Directory.Exists(local)) _projectsBasePath = local;
        }
        _user = user;
    }

    private (bool ok, string fullPath) ResolvePath(string path)
    {
        // Разделители — по ОС: на Windows '\', на Linux '/' (пути из API приходят с '/')
        path = Uri.UnescapeDataString(path);
        path = Path.DirectorySeparatorChar == '\\'
            ? path.Replace('/', '\\')
            : path.Replace('\\', '/');

        var fullBasePath = Path.GetFullPath(_projectsBasePath);
        var fullPath = Path.GetFullPath(path);

        var ok = fullPath.StartsWith(fullBasePath, StringComparison.OrdinalIgnoreCase)
                 && System.IO.File.Exists(fullPath);

        return (ok, fullPath);
    }

    // GET /api/file/download?path=... — скачивание копии (доступно всем на чтение)
    [HttpGet("download")]
    public IActionResult Download([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest("Путь не указан.");

        var (ok, fullPath) = ResolvePath(path);
        if (!ok) return NotFound("Файл не найден.");

        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(fullPath, out var contentType))
            contentType = "application/octet-stream";

        var fileName = Path.GetFileName(fullPath);
        return PhysicalFile(fullPath, contentType, fileName);
    }

    // GET /api/file/preview?path=... — предпросмотр в браузере без скачивания
    // отдельной копии (Content-Disposition: inline). Доступно всем на чтение,
    // как и Download; для типов, которые браузер не умеет показывать (Office),
    // возвращает 415 с понятным сообщением — скачайте оригинал.
    [HttpGet("preview")]
    public IActionResult Preview([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest("Путь не указан.");

        var (ok, fullPath) = ResolvePath(path);
        if (!ok) return NotFound("Файл не найден.");

        var ext = Path.GetExtension(fullPath);
        if (!InlinePreviewable.Contains(ext))
        {
            return StatusCode(415, new
            {
                message = "Предпросмотр этого типа файлов в браузере недоступен. " +
                           "Скачайте файл через /api/file/download.",
                extension = ext
            });
        }

        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(fullPath, out var contentType))
            contentType = "application/octet-stream";

        var stream = System.IO.File.OpenRead(fullPath);
        Response.Headers.ContentDisposition = "inline";
        return File(stream, contentType);
    }

    // GET /api/file/can-edit — доступ на чтение всем, на редактирование — только УБА.
    [HttpGet("can-edit")]
    public IActionResult CanEdit()
    {
        return Ok(new { canEdit = _user.IsUba, login = _user.Login });
    }
}
