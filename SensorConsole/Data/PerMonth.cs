using Waher.Persistence.Attributes;

namespace SensorConsole.Data
{
	/// <summary>
	/// Historic record, sampled every month
	/// </summary>
	[CollectionName("HistoryPerMonth")]
	public class PerMonth : HistoricRecordWithPeaks
	{
	}
}
