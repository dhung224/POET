using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.Web.CodeGenerators.Mvc.Templates.Blazor;
using POETWeb.Models.Domain;

namespace POET.Controllers
{
    public class ClassroomController
    {
        // Xử lý join
        [HttpPost, ValidateAntiForgeryToken]
        [Authorize(Roles = "Student")]
        public async Task<IActionResult> Join(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                ModelState.AddModelError(string.Empty, "Please enter class code.");
                return View();
            }

            var me = await _userManager.GetUserAsync(User);
            var normalized = code.Trim().ToUpper();

            var cls = await _db.Classrooms.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ClassCode == normalized);

            if (cls == null)
            {
                ModelState.AddModelError(string.Empty, "Class not found.");
                return View();
            }

            bool exists = await _db.Enrollments
                .AnyAsync(e => e.ClassId == cls.Id && e.UserId == me!.Id);

            if (!exists)
            {
                _db.Enrollments.Add(new Enrollment
                {
                    ClassId = cls.Id,
                    UserId = me!.Id,
                    RoleInClass = "Student",
                    JoinedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Details), new { id = cls.Id });
        }

        [Authorize(Roles = "Student")]
        public IActionResult JoinSuccess(int classId)
        {
            var cls = _db.Classrooms.AsNoTracking().FirstOrDefault(c => c.Id == classId);
            if (cls == null) return NotFound();

            ViewBag.ClassId = classId;
            ViewBag.ClassName = cls.Name;
            return View();
        }
    }
}
