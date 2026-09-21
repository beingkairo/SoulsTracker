using System.Text.Json;
using Microsoft.Data.Sqlite;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class SaveDirectoryPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChosenRootAndLegacyFileSelectionSurviveReopenWithoutChangingReaderIdentity(bool legacy)
    {
        string root = Path.Combine(Path.GetTempPath(), "SoulsTracker.DirectoryPersistence", Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "selected");
        string er = Path.Combine(directory, "account", "ER0000.sl2");
        string wk = Path.Combine(directory, "account", "ArchiveSaveFile.2.sav");
        string lp = Path.Combine(directory, "account", "SaveData-2_Character_2.sav");
        try
        {
            await using (var repository = new SqliteTrackerStateRepository(root, "test.db"))
            {
                await repository.SaveAsync(PersistentTrackerState.Default);
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "test.db"), Pooling = false }.ToString());
                await connection.OpenAsync();
                var fields = new Dictionary<string, object?> { ["SelectedGameId"] = GameId.EldenRing.Value, ["TotalEnabled"] = true, ["ShowGameName"] = true, ["EldenRingSavePath"] = er, ["EldenRingSaveSlotIndex"] = 3, ["BlackMythWukongSavePath"] = wk, ["LiesOfPSavePath"] = lp };
                if (!legacy)
                    foreach (string game in new[] { "EldenRing", "BlackMythWukong", "LiesOfP" }) fields[game + "SaveDirectory"] = directory;
                fields["EldenRingNoticeAcknowledged"] = true;
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE tracker_state SET payload=$payload WHERE id=1";
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(fields));
                await command.ExecuteNonQueryAsync();
                var loaded = Assert.IsType<PersistentTrackerState>((await repository.LoadAsync()).State);
                Assert.Equal(er, loaded.EldenRingSave.LocalPath);
                Assert.Equal(3, loaded.EldenRingSave.SlotIndex);
                Assert.Equal(wk, loaded.BlackMythWukongSave.LocalPath);
                Assert.Equal(lp, loaded.LiesOfPSave.LocalPath);
                foreach (object configuration in new object[] { loaded.EldenRingSave, loaded.BlackMythWukongSave, loaded.LiesOfPSave })
                {
                    var property = configuration.GetType().GetProperty("SelectedDirectory");
                    Assert.NotNull(property);
                    Assert.Equal(legacy ? null : directory, property.GetValue(configuration));
                }
                await repository.SaveAsync(loaded);
            }
            await using var reopened = new SqliteTrackerStateRepository(root, "test.db");
            var state = (await reopened.LoadAsync()).State!;
            Assert.Equal(er, state.EldenRingSave.LocalPath);
            Assert.Equal(3, state.EldenRingSave.SlotIndex);
            Assert.Equal(wk, state.BlackMythWukongSave.LocalPath);
            Assert.Equal(lp, state.LiesOfPSave.LocalPath);
            Assert.Equal(legacy ? null : directory, state.EldenRingSave.GetType().GetProperty("SelectedDirectory")!.GetValue(state.EldenRingSave));
            Assert.Equal(EffectiveDeathTotalResult.SourceIdentityFor(new PersistentTrackerState(1, GameId.EldenRing, OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true, eldenRingSave: new EldenRingSaveConfiguration(er, 3))), EffectiveDeathTotalResult.SourceIdentityFor(state));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
