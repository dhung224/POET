using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POETWeb.Data;
using POETWeb.Models;
using POETWeb.Models.Domain;
using System.Web; 

namespace POETWeb.Controllers
{
    [Authorize]
    public class MaterialsController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IWebHostEnvironment _env;

        // Danh sách file cho phép
        private readonly string[] _allowedExtensions = new[] { ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".jpg", ".jpeg", ".png", ".mp4", ".zip", ".rar" };

        public MaterialsController(ApplicationDbContext db, UserManager<ApplicationUser> userManager, IWebHostEnvironment env)
        {
            _db = db; _userManager = userManager; _env = env;
        }

        // ===== Helpers =====
        private async Task<bool> IsOwnerAsync(int classId)
        {
            var me = await _userManager.GetUserAsync(User);
            var cls = await _db.Classrooms.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId);
            return cls != null && me != null && cls.TeacherId == me.Id;
        }

        private async Task<bool> IsJoinedAsync(int classId)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return false;
            if (await IsOwnerAsync(classId)) return true;
            return await _db.Enrollments.AnyAsync(e => e.ClassId == classId && e.UserId == me.Id);
        }

        private string EnsureUploadDir()
        {
            var root = Path.Combine(_env.WebRootPath ?? "wwwroot", "uploads", "materials");
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            return root;
        }

        private static string? TryGetYouTubeId(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                var u = new Uri(url);
                if (u.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
                {
                    var q = HttpUtility.ParseQueryString(u.Query);
                    return q.Get("v");
                }
                if (u.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)) return u.AbsolutePath.Trim('/');
            }
            catch { }
            return null;
        }

        // ===== List theo class =====
        [HttpGet]
        public async Task<IActionResult> Index(int classId)
        {
            if (!await IsJoinedAsync(classId)) return Forbid();
            var cls = await _db.Classrooms.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId);
            if (cls == null) return NotFound();

            var list = await _db.Materials.AsNoTracking().Where(m => m.ClassId == classId).OrderByDescending(m => m.CreatedAt).ToListAsync();

            ViewBag.ClassId = classId;
            ViewBag.ClassName = cls.Name;
            ViewBag.ClassCode = cls.ClassCode;
            ViewBag.IsOwner = await IsOwnerAsync(classId);
            ViewBag.BackTo = User.IsInRole("Teacher") ? Url.Action("Index", "Teacher") : Url.Action("Index", "Student");
            return View(list);
        }

        // ===== Create =====
        [Authorize(Roles = "Teacher")]
        [HttpGet]
        public async Task<IActionResult> Create(int classId)
        {
            if (!await IsOwnerAsync(classId)) return Forbid();
            var cls = await _db.Classrooms.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId);
            if (cls == null) return NotFound();
            ViewBag.ClassId = classId;
            ViewBag.ClassName = cls.Name;
            return View(new Material { ClassId = classId });
        }

        [Authorize(Roles = "Teacher")]
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Material model, IFormFile? file, string? externalUrl)
        {
            //Trim dữ liệu
            model.IndexContent = model.IndexContent?.Trim();
            model.ExternalUrl = model.ExternalUrl?.Trim();

            if (model.ClassId <= 0) return BadRequest();
            if (!await IsOwnerAsync(model.ClassId)) return Forbid();

            var url = (externalUrl ?? model.ExternalUrl)?.Trim();
            var hasFile = file is { Length: > 0 };
            var hasUrl = !string.IsNullOrWhiteSpace(url);
            var hasIndex = !string.IsNullOrWhiteSpace(model.IndexContent);

            if (string.IsNullOrWhiteSpace(model.Title)) ModelState.AddModelError(nameof(Material.Title), "Title is required.");

            // Kiểm tra nội dung tối thiểu
            if (!hasFile && !hasUrl && !hasIndex)
                ModelState.AddModelError(string.Empty, "Provide at least one: File, URL or Index content.");

            //Kiểm tra đuôi file
            if (hasFile)
            {
                var ext = Path.GetExtension(file!.FileName).ToLowerInvariant();
                if (!_allowedExtensions.Contains(ext)) ModelState.AddModelError("file", $"File type {ext} not supported.");
            }

            ModelState.Remove(nameof(Material.FileUrl));
            ModelState.Remove(nameof(Material.OriginalFileName));
            ModelState.Remove(nameof(Material.FileSizeBytes));
            ModelState.Remove(nameof(Material.CreatedAt));
            ModelState.Remove(nameof(Material.Classroom));

            if (!ModelState.IsValid)
            {
                ViewBag.ClassId = model.ClassId;
                var cls = await _db.Classrooms.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.ClassId);
                ViewBag.ClassName = cls?.Name;
                return View(model);
            }

            if (hasFile)
            {
                var dir = EnsureUploadDir();
                var safe = $"{Guid.NewGuid():N}{Path.GetExtension(file!.FileName)}";
                var full = Path.Combine(dir, safe);
                using (var fs = System.IO.File.Create(full)) await file.CopyToAsync(fs);
                model.FileUrl = $"/uploads/materials/{safe}";
                model.OriginalFileName = Path.GetFileName(file.FileName);
                model.FileSizeBytes = file.Length;
                model.Provider = "Local"; model.MediaKind = "file";
            }
            if (hasUrl)
            {
                model.ExternalUrl = url;
                var you = TryGetYouTubeId(url);
                if (!string.IsNullOrEmpty(you)) { model.Provider = "YouTube"; model.MediaKind = "video"; model.ThumbnailUrl = $"https://img.youtube.com/vi/{you}/hqdefault.jpg"; }
                else { model.Provider = "Link"; model.MediaKind = "link"; }
            }
            if (!hasFile && !hasUrl && hasIndex) { model.Provider = "Index"; model.MediaKind = "note"; }

            var me = await _userManager.GetUserAsync(User);
            model.CreatedById = me?.Id;
            model.CreatedAt = DateTime.UtcNow;

            _db.Materials.Add(model);
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index), new { classId = model.ClassId });
        }

        // ===== Edit =====
        [Authorize(Roles = "Teacher")]
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var m = await _db.Materials.FindAsync(id);
            if (m == null) return NotFound();
            if (!await IsOwnerAsync(m.ClassId)) return Forbid();
            ViewBag.ClassId = m.ClassId;
            var cls = await _db.Classrooms.AsNoTracking().FirstOrDefaultAsync(c => c.Id == m.ClassId);
            ViewBag.ClassName = cls?.Name;
            return View(m);
        }

        [Authorize(Roles = "Teacher")]
        [HttpPost, ValidateAntiForgeryToken]
        // FIX: Thêm tham số removeFile
        public async Task<IActionResult> Edit(int id, Material input, IFormFile? file, string? externalUrl, bool removeFile = false)
        {
            input.IndexContent = input.IndexContent?.Trim();
            input.ExternalUrl = input.ExternalUrl?.Trim();

            var m = await _db.Materials.FirstOrDefaultAsync(x => x.Id == id);
            if (m == null) return NotFound();
            if (!await IsOwnerAsync(m.ClassId)) return Forbid();

            if (string.IsNullOrWhiteSpace(input.Title)) ModelState.AddModelError(nameof(Material.Title), "Title is required.");

            var url = (externalUrl ?? input.ExternalUrl)?.Trim();
            var hasFile = file is { Length: > 0 };
            var hasUrl = !string.IsNullOrWhiteSpace(url);
            var hasIndex = !string.IsNullOrWhiteSpace(input.IndexContent);

            // FIX: Logic kiểm tra nội dung (tính cả file cũ)
            bool hasExistingFile = !string.IsNullOrWhiteSpace(m.FileUrl);
            bool contentExists = hasUrl || hasIndex || hasFile || (hasExistingFile && !removeFile);

            if (!contentExists) ModelState.AddModelError(string.Empty, "Provide at least one: File, URL or Index content.");

            if (hasFile)
            {
                var ext = Path.GetExtension(file!.FileName).ToLowerInvariant();
                if (!_allowedExtensions.Contains(ext)) ModelState.AddModelError("file", $"File type {ext} not supported.");
            }

            if (!ModelState.IsValid)
            {
                ViewBag.ClassId = m.ClassId;
                var cls = await _db.Classrooms.AsNoTracking().FirstOrDefaultAsync(c => c.Id == m.ClassId);
                ViewBag.ClassName = cls?.Name;
                return View(m);
            }

            m.Title = input.Title;
            m.Description = input.Description;
            m.Category = input.Category;
            m.IndexContent = input.IndexContent;
            m.UpdatedAt = DateTime.UtcNow;

            string oldFileUrl = m.FileUrl;
            bool shouldDeleteOld = (hasFile || removeFile) && !string.IsNullOrWhiteSpace(oldFileUrl);

            if (hasFile)
            {
                var dir = EnsureUploadDir();
                var safe = $"{Guid.NewGuid():N}{Path.GetExtension(file!.FileName)}";
                var full = Path.Combine(dir, safe);
                using (var fs = System.IO.File.Create(full)) await file.CopyToAsync(fs);
                m.FileUrl = $"/uploads/materials/{safe}";
                m.OriginalFileName = Path.GetFileName(file.FileName);
                m.FileSizeBytes = file.Length;
                if (string.IsNullOrEmpty(m.Provider)) m.Provider = "Local";
                if (string.IsNullOrEmpty(m.MediaKind)) m.MediaKind = "file";
            }
            else if (removeFile) { m.FileUrl = null; m.OriginalFileName = null; m.FileSizeBytes = null; }

            if (hasUrl)
            {
                m.ExternalUrl = url;
                var you = TryGetYouTubeId(url);
                if (!string.IsNullOrEmpty(you)) { m.Provider = "YouTube"; m.MediaKind = "video"; m.ThumbnailUrl = $"https://img.youtube.com/vi/{you}/hqdefault.jpg"; }
                else { if (string.IsNullOrEmpty(m.Provider) || m.Provider == "Local") m.Provider = "Link"; if (string.IsNullOrEmpty(m.MediaKind) || m.MediaKind == "file") m.MediaKind = "link"; m.ThumbnailUrl = null; }
            }
            else { m.ExternalUrl = null; m.ThumbnailUrl = null; } // FIX: Xóa URL cũ nếu trống

            if (string.IsNullOrWhiteSpace(m.FileUrl) && string.IsNullOrWhiteSpace(m.ExternalUrl) && hasIndex) { m.Provider = "Index"; m.MediaKind = "note"; }

            await _db.SaveChangesAsync();

            if (shouldDeleteOld)
            {
                var phys = Path.Combine(_env.WebRootPath ?? "wwwroot", oldFileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                try { if (System.IO.File.Exists(phys)) System.IO.File.Delete(phys); } catch { }
            }
            return RedirectToAction(nameof(Index), new { classId = m.ClassId });
        }

        // ===== Delete =====
        [Authorize(Roles = "Teacher")]
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var m = await _db.Materials.FirstOrDefaultAsync(x => x.Id == id);
            if (m == null) return NotFound();
            if (!await IsOwnerAsync(m.ClassId)) return Forbid();

            if (!string.IsNullOrWhiteSpace(m.FileUrl))
            {
                var phys = Path.Combine(_env.WebRootPath ?? "wwwroot", m.FileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                try { if (System.IO.File.Exists(phys)) System.IO.File.Delete(phys); } catch { }
            }
            var cid = m.ClassId;
            _db.Materials.Remove(m);
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index), new { classId = cid });
        }
    }
}