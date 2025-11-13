using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POETWeb.Data;
using POETWeb.Models;
using POETWeb.Models.Enums;
using POETWeb.Models.ViewModels;
using System.Globalization;

namespace POETWeb.Controllers
{
    [Authorize(Roles = "Teacher")]
    public class TeacherController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _db;

        public TeacherController(UserManager<ApplicationUser> userManager, ApplicationDbContext db)
        {
            _userManager = userManager;
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            var teacherId = user.Id;

            // 1) Classes
            var classes = await _db.Classrooms
                .Where(c => c.TeacherId == teacherId)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new ClassCardVM
                {
                    Id = c.Id,
                    Title = c.Name,
                    Students = c.Enrollments!.Count(e => e.RoleInClass == "Student"),
                    Subject = c.Subject
                })
                .ToListAsync();

            // 2) Distinct students
            var totalStudents = await _db.Enrollments
                .Where(e => e.Classroom!.TeacherId == teacherId && e.RoleInClass == "Student")
                .Select(e => e.UserId)
                .Distinct()
                .CountAsync();

            // 3) Assignments count
            var assignmentsCount = await _db.Assignments
                .AsNoTracking()
                .CountAsync(a => a.Class.TeacherId == teacherId);

            // 4) Pending manual grading
            var pendingGrades = await _db.AssignmentAttempts
                .AsNoTracking()
                .Where(t => t.Assignment.Class.TeacherId == teacherId
                         && t.RequiresManualGrading
                         && t.SubmittedAt != null
                         && t.FinalScore == null)
                .CountAsync();

            // ===== NOTICES: submissions + needs-grading + joined (top 10) =====

            // Submissions mới nhất (lấy dư ra rồi Top 10 sau)
            var subRaw = await _db.AssignmentAttempts
                .AsNoTracking()
                .Where(t => t.Assignment.Class.TeacherId == teacherId && t.SubmittedAt != null)
                .OrderByDescending(t => t.SubmittedAt)
                .Select(t => new
                {
                    t.UserId,
                    t.SubmittedAt, // DateTimeOffset?
                    ClassTitle = t.Assignment.Class.Name,
                    AssignmentTitle = t.Assignment.Title,
                    NeedsGrading = t.RequiresManualGrading && t.FinalScore == null
                })
                .Take(30)
                .ToListAsync();

            // Học sinh mới join (Enrollment có JoinedAt: DateTime)
            var joinRaw = await _db.Enrollments
                .AsNoTracking()
                .Where(e => e.Classroom!.TeacherId == teacherId
                         && e.RoleInClass == "Student")
                .OrderByDescending(e => e.JoinedAt == default(DateTime) ? DateTime.UtcNow : e.JoinedAt)
                .Select(e => new
                {
                    e.UserId,
                    e.JoinedAt, // DateTime
                    ClassTitle = e.Classroom!.Name
                })
                .Take(30)
                .ToListAsync();

            // Lấy tên hiển thị
            var userIds = subRaw.Select(x => x.UserId).Concat(joinRaw.Select(x => x.UserId)).Distinct().ToList();
            var users = await _db.Users
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.UserName })
                .ToListAsync();

            string DisplayName(string? full, string? username)
                => string.IsNullOrWhiteSpace(full) ? (username ?? "Student") : full!;

            var notices = new List<TeacherNoticeVM>();

            // Map submissions
            notices.AddRange(subRaw.Select(s =>
            {
                var u = users.FirstOrDefault(x => x.Id == s.UserId);
                var when = s.SubmittedAt ?? DateTimeOffset.UtcNow;
                return new TeacherNoticeVM
                {
                    Kind = s.NeedsGrading ? TeacherNoticeKind.NeedsGrading : TeacherNoticeKind.Submission,
                    StudentName = DisplayName(u?.FullName, u?.UserName),
                    AssignmentTitle = s.AssignmentTitle ?? "(assignment)",
                    ClassName = s.ClassTitle ?? "(class)",
                    When = when
                };
            }));

            // Map joins (JoinedAt là DateTime)
            notices.AddRange(joinRaw.Select(j =>
            {
                var u = users.FirstOrDefault(x => x.Id == j.UserId);
                var when = ToDto(j.JoinedAt);
                return new TeacherNoticeVM
                {
                    Kind = TeacherNoticeKind.JoinedClass,
                    StudentName = DisplayName(u?.FullName, u?.UserName),
                    AssignmentTitle = "",
                    ClassName = j.ClassTitle ?? "(class)",
                    When = when
                };
            }));

            var top10 = notices
                .OrderByDescending(n => n.When)
                .Take(10)
                .ToList();

            var vm = new TeacherDashboardVM
            {
                FirstName = ExtractFirstName(user.FullName),
                ActiveClasses = classes.Count,
                TotalStudents = totalStudents,
                Assignments = assignmentsCount,
                PendingGrades = pendingGrades,
                Classes = classes,
                Notices = top10
            };

            return View(vm);
        }

        // Helpers
        private static DateTimeOffset ToDto(object? raw)
        {
            if (raw is DateTimeOffset dto) return dto;
            if (raw is DateTime dt)
            {
                return dt.Kind switch
                {
                    DateTimeKind.Utc => new DateTimeOffset(dt, TimeSpan.Zero),
                    DateTimeKind.Local => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
                    _ => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero)
                };
            }
            return DateTimeOffset.UtcNow;
        }

        private static string ExtractFirstName(string? fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "Teacher";
            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : "Teacher";
        }
    }
}
