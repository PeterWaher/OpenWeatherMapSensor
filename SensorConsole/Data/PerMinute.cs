using Waher.Persistence.Attributes;

namespace SensorConsole.Data
{
	/// <summary>
	/// Historic record, sampled every minute
	/// </summary>
	[CollectionName("HistoryPerMinute")]
	public class PerMinute : HistoricRecord
	{
	}
}
