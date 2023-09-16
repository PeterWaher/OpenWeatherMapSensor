using Waher.Persistence.Attributes;

namespace SensorConsole.Data
{
	/// <summary>
	/// Historic record, sampled every week
	/// </summary>
	[CollectionName("HistoryPerWeek")]
	public class PerWeek : HistoricRecordWithPeaks
	{
	}
}
