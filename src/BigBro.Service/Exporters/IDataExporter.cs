using BigBro.Common.Data;

namespace BigBro.Service.Exporters;

public interface IDataExporter
{
    void Export(SqliteStore store);
}
