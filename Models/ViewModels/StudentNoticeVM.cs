namespace POETWeb.Models.ViewModels
{
    public class UiNotice
    {
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string? Actor { get; set; }
        public DateTimeOffset When { get; set; }
    }
}
