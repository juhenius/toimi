using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Semantic;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityBehaviorTests
{
  private static Entity NewEntity(TietueDbContext db, string type = "note", string json = /*lang=json,strict*/ """{"content":"hello"}""")
  {
    var e = new Entity
    {
      Id = Guid.NewGuid(),
      Type = type,
      Data = JsonDocument.Parse(json),
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
    db.Entities.Add(e);
    return e;
  }

  private static BehaviorContext NewContext(Entity e, TypeBehaviors behaviors, EntityOperation op = EntityOperation.Create, string? defaultTriggers = null, bool dataChanged = true)
  {
    return new BehaviorContext
    {
      Entity = e,
      Operation = op,
      Behaviors = behaviors,
      DefaultTriggersJson = defaultTriggers,
      Now = e.CreatedAt,
      DataChanged = dataChanged,
    };
  }

  private const string SemanticBehaviors = /*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["content"]}}]""";

  [Fact]
  public async Task Semantic_behavior_enqueues_on_saving_and_drains_on_committed()
  {
    using var db = TestDb.New();
    await new Types.TypeRepository(db).DefineAsync("note", /*lang=json,strict*/ """{"type":"object"}""", SemanticBehaviors);
    var idx = new FakeSemanticIndex();
    var behavior = new SemanticIndexBehavior(new SemanticOutbox(db, idx));
    var e = NewEntity(db);
    var ctx = NewContext(e, TypeBehaviors.Parse(SemanticBehaviors));

    await behavior.OnSavingAsync(ctx, default);
    await db.SaveChangesAsync();
    Assert.Single(await db.IndexOutbox.ToListAsync()); // row rode the entity's save

    await behavior.OnCommittedAsync(ctx, default);
    Assert.Equal("hello", idx.Store["note"][e.Id]);
    Assert.Empty(await db.IndexOutbox.ToListAsync()); // drained
  }

  [Fact]
  public async Task Semantic_behavior_skips_unindexed_types_and_tags_only_updates()
  {
    using var db = TestDb.New();
    var idx = new FakeSemanticIndex();
    var behavior = new SemanticIndexBehavior(new SemanticOutbox(db, idx));
    var e = NewEntity(db);

    await behavior.OnSavingAsync(NewContext(e, TypeBehaviors.None), default);
    await behavior.OnSavingAsync(NewContext(e, TypeBehaviors.Parse(SemanticBehaviors), EntityOperation.Update, dataChanged: false), default);
    await db.SaveChangesAsync();

    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Semantic_behavior_enqueues_delete_op_on_delete()
  {
    using var db = TestDb.New();
    var idx = new FakeSemanticIndex();
    var behavior = new SemanticIndexBehavior(new SemanticOutbox(db, idx));
    var e = NewEntity(db);
    var ctx = NewContext(e, TypeBehaviors.Parse(SemanticBehaviors), EntityOperation.Delete);

    await behavior.OnSavingAsync(ctx, default);
    await db.SaveChangesAsync();

    Assert.Equal("delete", (await db.IndexOutbox.SingleAsync()).Op);
  }

  [Fact]
  public async Task Provisioning_behavior_provisions_on_create_only()
  {
    using var db = TestDb.New();
    var behavior = new TriggerProvisioningBehavior(new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default)));
    var e = NewEntity(db, json: /*lang=json,strict*/ """{"dueAt":"2026-09-01T09:00:00Z"}""");
    await db.SaveChangesAsync();
    const string defaults = /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify"}}]""";

    await behavior.OnSavedAsync(NewContext(e, TypeBehaviors.None, EntityOperation.Update, defaults), default);
    Assert.Empty(await db.Triggers.ToListAsync());

    await behavior.OnSavedAsync(NewContext(e, TypeBehaviors.None, EntityOperation.Create, defaults), default);
    Assert.Equal("notify", (await db.Triggers.SingleAsync()).HandlerKind);
  }

  [Fact]
  public async Task Expiry_behavior_arms_on_create_and_disarms_when_config_absent()
  {
    using var db = TestDb.New();
    var behavior = new ExpiryBehavior(new ExpiryReconciler(db, new TriggerRepository(db, TestConfig.Default)));
    var e = NewEntity(db, json: /*lang=json,strict*/ """{"expiresAt":"2026-09-01T00:00:00Z"}""");
    await db.SaveChangesAsync();
    var withExpiry = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"expiresAt"}}]""");

    await behavior.OnSavedAsync(NewContext(e, withExpiry), default);
    Assert.Equal("delete", (await db.Triggers.SingleAsync(t => t.Source == "expiry")).HandlerKind);

    // Behavior removed from the type: reconcile must still run and remove the stale trigger.
    await behavior.OnSavedAsync(NewContext(e, TypeBehaviors.None, EntityOperation.Update), default);
    Assert.Empty(await db.Triggers.Where(t => t.Source == "expiry").ToListAsync());
  }

  [Fact]
  public async Task Expiry_behavior_skips_delete_and_tags_only_update()
  {
    using var db = TestDb.New();
    var behavior = new ExpiryBehavior(new ExpiryReconciler(db, new TriggerRepository(db, TestConfig.Default)));
    var e = NewEntity(db, json: /*lang=json,strict*/ """{"expiresAt":"2026-09-01T00:00:00Z"}""");
    await db.SaveChangesAsync();
    var withExpiry = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"expiresAt"}}]""");

    await behavior.OnSavedAsync(NewContext(e, withExpiry, EntityOperation.Delete), default);
    await behavior.OnSavedAsync(NewContext(e, withExpiry, EntityOperation.Update, dataChanged: false), default);

    Assert.Empty(await db.Triggers.ToListAsync());
  }
}
