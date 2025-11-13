using System;

namespace POETWeb.Models.ViewModels
{
    public enum TeacherNoticeKind
    {
        Submission,
        NeedsGrading,
        JoinedClass
    }

    public sealed class TeacherNoticeVM
    {
        public TeacherNoticeKind Kind { get; set; }

        public string StudentName { get; set; } = "";
        public string AssignmentTitle { get; set; } = "";
        public string ClassName { get; set; } = "";

        public DateTimeOffset When { get; set; }
        public string WhenText => When.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    }
}
