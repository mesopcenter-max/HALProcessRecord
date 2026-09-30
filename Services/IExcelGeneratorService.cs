using HALProcessRecord.ViewModels;

namespace HALProcessRecord.Services;

public interface IExcelGeneratorService
{
    byte[] Generate(ProcessRecordViewModel model);
}
