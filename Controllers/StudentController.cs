using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POETWeb.Data;
using POETWeb.Models;
using POETWeb.Models.Enums;
using POETWeb.Models.ViewModels;

namespace POETWeb.Controllers
{
    [Authorize]
    public class StudentController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;

        public StudentController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
        {
            _db = db;
            _userManager = userManager;
        }

        // Dashboard
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Challenge();

            // Lấy các class đã join + thông tin giáo viên
            var classes = await (
                from e in _db.Enrollments.AsNoTracking()
                join c in _db.Classrooms.AsNoTracking() on e.ClassId equals c.Id
                join t in _db.Users.AsNoTracking() on c.TeacherId equals t.Id
                where e.UserId == me.Id
                orderby c.CreatedAt descending
                select new
                {
                    c.Id,
                    Title = c.Name,
                    c.Subject,
                    TeacherName = string.IsNullOrWhiteSpace(t.FullName) ? (t.UserName ?? "Teacher") : t.FullName!,
                    t.AvatarUrl
                }
            ).ToListAsync();

            var classIds = classes.Select(x => x.Id).ToList();

            // Đếm học viên mỗi lớp bằng 1 query nhóm
            var studentCounts = await _db.Enrollments.AsNoTracking()
                .Where(en => classIds.Contains(en.ClassId))
                .GroupBy(en => en.ClassId)
                .Select(g => new { ClassId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ClassId, x => x.Count);

            // Tổng materials của tất cả lớp đã join
            var materialsCount = await _db.Materials.AsNoTracking()
                .CountAsync(m => classIds.Contains(m.ClassId));

            // “Active classes (7d)”
            var activeThisWeek = await _db.Materials.AsNoTracking()
                .Where(m => classIds.Contains(m.ClassId) &&
                            m.CreatedAt >= DateTime.UtcNow.AddDays(-7))
                .Select(m => m.ClassId)
                .Distinct()
                .CountAsync();

            var vm = new StudentDashboardVM
            {
                FirstName = ExtractFirstName(me.FullName) ?? (me.UserName ?? "Student"),
                JoinedClasses = classes.Count,
                AssignmentsDue = 0,
                MaterialsPosted = materialsCount,
                HoursLearned = activeThisWeek,
                Classes = classes.Select(c => new StudentClassCardVM
                {
                    Id = c.Id,
                    Title = c.Title,
                    Subject = c.Subject,
                    TeacherName = c.TeacherName,
                    TeacherAvatar = c.AvatarUrl,
                    Students = studentCounts.TryGetValue(c.Id, out var n) ? n : 0
                }).ToList()
            };

            var userId = me.Id;
            var now = DateTimeOffset.UtcNow;
            var soon = now.AddHours(5);

            // Tên lớp map nhanh
            var classNames = await _db.Classrooms.AsNoTracking()
                .Where(c => classIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name);

            // 1) Assignments sắp hết hạn trong 5 giờ, user CHƯA có attempt nào
            var expiringRaw = await _db.Assignments.AsNoTracking()
                .Where(a => classIds.Contains(a.ClassId)
                         && a.OpenAt <= soon
                         && a.CloseAt != null && a.CloseAt > now && a.CloseAt <= soon)
                .Select(a => new { a.Id, a.Title, a.ClassId, a.CloseAt })
                .ToListAsync();

            var expiringIds = expiringRaw.Select(x => x.Id).ToList();

            var attemptedAssIds = await _db.AssignmentAttempts.AsNoTracking()
                .Where(t => t.UserId == userId && expiringIds.Contains(t.AssignmentId))
                .Select(t => t.AssignmentId)
                .Distinct()
                .ToListAsync();

            var expiringNotices = expiringRaw
                .Where(x => !attemptedAssIds.Contains(x.Id))
                .Select(x => new UiNotice
                {
                    Kind = "deadline",
                    Title = x.Title,
                    ClassName = classNames.TryGetValue(x.ClassId, out var cn) ? cn : $"Class #{x.ClassId}",
                    Actor = null,
                    When = x.CloseAt!.Value
                })
                .ToList();

            // 2) Trạng thái chấm cho Essay/Mixed: graded hay pending (lấy attempt mới nhất theo SubmittedAt)
            var attemptsRaw = await _db.AssignmentAttempts.AsNoTracking()
                .Where(t => t.UserId == userId
                         && classIds.Contains(t.Assignment.ClassId)
                         && (t.Assignment.Type == AssignmentType.Essay || t.Assignment.Type == AssignmentType.Mixed))
                .Select(t => new
                {
                    t.AssignmentId,
                    t.Assignment.Title,
                    t.Assignment.ClassId,
                    t.SubmittedAt,
                    t.FinalScore,
                    t.Status
                })
                .ToListAsync();

            var latestByAssignment = attemptsRaw
                .GroupBy(x => x.AssignmentId)
                .Select(g => g.OrderByDescending(x => x.SubmittedAt ?? DateTimeOffset.MinValue).First())
                .ToList();

            var gradingNotices = latestByAssignment
                .Select(x => new UiNotice
                {
                    Kind = (x.FinalScore.HasValue || x.Status == AttemptStatus.Graded) ? "graded" : "pending-grade",
                    Title = x.Title,
                    ClassName = classNames.TryGetValue(x.ClassId, out var cn) ? cn : $"Class #{x.ClassId}",
                    Actor = null,
                    When = x.SubmittedAt ?? now
                })
                .ToList();

            // 3) Materials mới trong 7 ngày (kèm giáo viên)
            var materialsRaw = await (
                from m in _db.Materials.AsNoTracking()
                where classIds.Contains(m.ClassId) && m.CreatedAt >= now.AddDays(-7)
                join u in _db.Users.AsNoTracking() on m.CreatedById equals u.Id into ug
                from u in ug.DefaultIfEmpty()
                select new
                {
                    m.Title,
                    m.ClassId,
                    m.CreatedAt,
                    TeacherFullName = u.FullName,
                    TeacherUserName = u.UserName
                }
            ).ToListAsync();

            var materialNotices = materialsRaw
                .Select(x => new UiNotice
                {
                    Kind = "material",
                    Title = x.Title,
                    ClassName = classNames.TryGetValue(x.ClassId, out var cn) ? cn : $"Class #{x.ClassId}",
                    Actor = !string.IsNullOrWhiteSpace(x.TeacherFullName) ? x.TeacherFullName : (x.TeacherUserName ?? "Teacher"),
                    When = x.CreatedAt
                })
                .OrderByDescending(n => n.When)
                .ToList();

            // 4) Assignments mới trong 7 ngày (kèm giáo viên)
            var newAssignRaw = await (
                from a in _db.Assignments.AsNoTracking()
                where classIds.Contains(a.ClassId) && a.CreatedAt >= now.AddDays(-7)
                join u in _db.Users.AsNoTracking() on a.CreatedById equals u.Id into ug
                from u in ug.DefaultIfEmpty()
                select new
                {
                    a.Title,
                    a.ClassId,
                    a.Type,
                    a.CreatedAt,
                    TeacherFullName = u.FullName,
                    TeacherUserName = u.UserName
                }
            ).ToListAsync();

            var newAssignmentNotices = newAssignRaw
                .Select(x => new UiNotice
                {
                    Kind = "new-assignment",
                    Title = $"{x.Title} ({x.Type})",
                    ClassName = classNames.TryGetValue(x.ClassId, out var cn) ? cn : $"Class #{x.ClassId}",
                    Actor = !string.IsNullOrWhiteSpace(x.TeacherFullName) ? x.TeacherFullName : (x.TeacherUserName ?? "Teacher"),
                    When = x.CreatedAt
                })
                .OrderByDescending(n => n.When)
                .ToList();

            // Gộp + sort
            vm.Notices = expiringNotices
                .Concat(gradingNotices)
                .Concat(materialNotices)
                .Concat(newAssignmentNotices)
                .OrderByDescending(n => n.When)
                .Take(20)
                .ToList();

            return View(vm);
        }

        private static string? ExtractFirstName(string? fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return null;
            var p = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return p.Length == 0 ? null : p[^1];
        }

        public sealed class JoinDto
        {
            [Required] public string Code { get; set; } = "";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> JoinByCode([FromForm] JoinDto dto)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized(new { ok = false, message = "Unauthorized" });

            if (!ModelState.IsValid)
                return BadRequest(new { ok = false, message = "Code is required." });

            var code = dto.Code.Trim().ToUpperInvariant();

            var cls = await _db.Classrooms.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ClassCode == code);

            if (cls == null)
                return NotFound(new { ok = false, message = "Class not found." });

            var exists = await _db.Enrollments
                .AnyAsync(e => e.ClassId == cls.Id && e.UserId == me.Id);

            if (!exists)
            {
                _db.Enrollments.Add(new POETWeb.Models.Domain.Enrollment
                {
                    ClassId = cls.Id,
                    UserId = me.Id,
                    RoleInClass = "Student",
                    JoinedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
            }

            return Json(new
            {
                ok = true,
                classId = cls.Id,
                className = cls.Name,
                code = cls.ClassCode
            });
        }

        // ===== Quick View (Student) =====
        [HttpGet]
        public async Task<IActionResult> QuickView(int id)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var cls = await _db.Classrooms
                .AsNoTracking()
                .Include(c => c.Teacher)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (cls == null) return NotFound();

            // đảm bảo đã join lớp
            var joined = await _db.Enrollments.AnyAsync(e => e.ClassId == id && e.UserId == me.Id);
            if (!joined) return Forbid();

            var vm = new ClassQuickViewVM
            {
                Id = cls.Id,
                Title = cls.Name,
                Subject = cls.Subject,
                ClassCode = cls.ClassCode,
                TeacherName = string.IsNullOrWhiteSpace(cls.Teacher?.FullName)
                    ? (cls.Teacher?.UserName ?? "Teacher")
                    : cls.Teacher!.FullName!,
                TeacherAvatar = cls.Teacher?.AvatarUrl
            };

            return PartialView("_StudentQuickView", vm);
        }

        // ===== Leave class (POST) =====
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Leave(int classId)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var enr = await _db.Enrollments.FirstOrDefaultAsync(e => e.ClassId == classId && e.UserId == me.Id);
            if (enr == null) return NotFound(new { ok = false, message = "You are not in this class." });

            _db.Enrollments.Remove(enr);
            await _db.SaveChangesAsync();

            return Json(new { ok = true });
        }

        // ===== QuickView: Student xem chi tiết lớp (partial) =====
        [HttpGet]
        public async Task<IActionResult> DetailsPartial(int id)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            // Chỉ cho xem nếu đã join lớp
            var enrolled = await _db.Enrollments
                .AsNoTracking()
                .AnyAsync(e => e.ClassId == id && e.UserId == me.Id);
            if (!enrolled) return Forbid();

            var cls = await _db.Classrooms
                .AsNoTracking()
                .Include(c => c.Teacher)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (cls == null) return NotFound();

            var vm = new ClassQuickViewVM
            {
                Id = cls.Id,
                Title = cls.Name,
                ClassCode = cls.ClassCode,
                Subject = cls.Subject,
                TeacherName = string.IsNullOrWhiteSpace(cls.Teacher?.FullName) ? "Unknown" : cls.Teacher!.FullName!,
                TeacherAvatar = cls.Teacher?.AvatarUrl
            };

            // Trả đúng tên partial view của cậu
            return PartialView("_StudentQuickView", vm);
        }

        [HttpGet]
        public IActionResult Classes() => RedirectToAction(nameof(Index));
    }
}
