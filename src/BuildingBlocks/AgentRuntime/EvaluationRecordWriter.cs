using System.Text;
using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// Persists evaluation records for later analysis. Writers must be safe under
/// concurrent writes and must not lose completed records on process exit.
/// </summary>
public interface IEvaluationRecordWriter
{
    /// <summary>Writes a single evaluation record.</summary>
    /// <param name="record">The record to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task WriteAsync(AgentEvaluationRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Writes evaluation records as JSON Lines (one canonical-JSON document per
/// line) into a dated file under the configured directory, by default
/// <c>results/evaluations/</c>. Each record is serialized fully before the file
/// is touched and written with a single flushed append inside a process-wide
/// lock, so concurrent writers cannot interleave and a failed serialization
/// cannot leave a partial line behind.
/// </summary>
public sealed class JsonLinesEvaluationRecordWriter : IEvaluationRecordWriter
{
    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Initializes a new JSON Lines writer.</summary>
    /// <param name="directory">The output directory, created on first write when missing.</param>
    /// <param name="timeProvider">The time provider used for file naming. Defaults to <see cref="TimeProvider.System"/>.</param>
    public JsonLinesEvaluationRecordWriter(string directory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task WriteAsync(AgentEvaluationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Serialize before acquiring the lock so a serialization failure never
        // produces a partial line in the output file.
        byte[] line = Encoding.UTF8.GetBytes(ContractSerialization.Serialize(record) + "\n");
        string path = Path.Combine(
            _directory,
            $"evaluations-{_timeProvider.GetUtcNow():yyyyMMdd}.jsonl");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
