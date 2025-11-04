using System.Collections.Generic;

namespace POET.Models.ViewModels
{
    public class AssignmentTeacherVM
    {
        public int? ClassId { get; set; }
        public string? ClassName { get; set; }
        public List<AssignmentListItemVM> Items { get; set; } = new();
    }
}
