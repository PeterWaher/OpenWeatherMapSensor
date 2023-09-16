using Waher.Persistence.Attributes;

namespace SensorConsole.Data
{
	/// <summary>
	/// Historic record, sampled every day
	/// </summary>
	[CollectionName("HistoryPerDay")]
	public class PerDay : HistoricRecordWithPeaks
	{
	}
}
