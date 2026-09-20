namespace VoiceOS.Core.Apps;

public interface IAppCatalog
{
    IReadOnlyList<AppEntry> GetAll();
    AppEntry? FindById(string id);
}
