using Waher.Persistence.Attributes;

namespace SensorConsole.Data
{
	/// <summary>
	/// Historic record, sampled every hour
	/// </summary>
	[CollectionName("HistoryPerHour")]
	public class PerHour : HistoricRecordWithPeaks
	{
	}
}
