using System.Collections.Generic;

namespace NetPad.Data;

public interface IDataConnectionSchemaChangeDetectionStrategyFactory
{
    IEnumerable<IDataConnectionSchemaChangeDetectionStrategy> GetStrategies(DataConnection dataConnection);
}
