using MNVTEngine.Core.Timing;

namespace MNVTEngine.ECS.Tests;

public class WorldTests
{
    private struct Position
    {
        public float X;
    }

    private struct Velocity;

    private sealed class Tag;

    [Fact]
    public void Entities_HaveGenerationsAndStaleHandlesDie()
    {
        var world = new World();
        var a = world.CreateEntity();
        Assert.False(a.IsNull);
        Assert.True(world.IsAlive(a));

        Assert.True(world.DestroyEntity(a));
        Assert.False(world.IsAlive(a));
        Assert.False(world.DestroyEntity(a));

        var b = world.CreateEntity();
        Assert.Equal(a.Index, b.Index);
        Assert.NotEqual(a.Generation, b.Generation);
        Assert.False(world.IsAlive(a));
        Assert.True(world.IsAlive(b));
        Assert.False(world.IsAlive(Entity.Null));
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void StructComponents_AreMutableByRef()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.Add(e, new Position { X = 1 });
        world.Get<Position>(e).X += 5;
        Assert.Equal(6, world.Get<Position>(e).X);
        Assert.True(world.TryGet<Position>(e, out var p));
        Assert.Equal(6, p.X);
    }

    [Fact]
    public void AddTwice_AndAccessMissing_Throw()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.Add(e, new Position());
        Assert.Throws<InvalidOperationException>(() => world.Add(e, new Position()));
        Assert.Throws<KeyNotFoundException>(() => world.Get<Velocity>(e));
        Assert.False(world.TryGet<Velocity>(e, out _));
    }

    [Fact]
    public void OperationsOnDeadEntities_Throw()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.DestroyEntity(e);
        Assert.Throws<InvalidOperationException>(() => world.Add(e, new Position()));
        Assert.False(world.Has<Position>(e));
    }

    [Fact]
    public void DestroyEntity_RemovesAllComponents()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.Add(e, new Position());
        world.Add(e, new Tag());
        world.DestroyEntity(e);
        Assert.Equal(0, world.Pool<Position>().Count);
        Assert.Equal(0, world.Pool<Tag>().Count);

        var reused = world.CreateEntity();
        Assert.False(world.Has<Position>(reused));
    }

    [Fact]
    public void RemoveSwapsLastIntoHole_KeepingLookupsCorrect()
    {
        var world = new World();
        var entities = Enumerable.Range(0, 5).Select(i =>
        {
            var e = world.CreateEntity();
            world.Add(e, new Position { X = i });
            return e;
        }).ToArray();

        world.Remove<Position>(entities[1]);
        Assert.Equal(4, world.Pool<Position>().Count);
        for (var i = 0; i < 5; i++)
        {
            if (i == 1)
            {
                Assert.False(world.Has<Position>(entities[i]));
            }
            else
            {
                Assert.Equal(i, world.Get<Position>(entities[i]).X);
            }
        }
    }

    [Fact]
    public void Query_ReturnsOnlyEntitiesWithAllComponents()
    {
        var world = new World();
        var both = world.CreateEntity();
        world.Add(both, new Position());
        world.Add(both, new Velocity());
        var onlyPosition = world.CreateEntity();
        world.Add(onlyPosition, new Position());
        var all = world.CreateEntity();
        world.Add(all, new Position());
        world.Add(all, new Velocity());
        world.Add(all, new Tag());

        Assert.Equal(3, Collect(world.Query<Position>()).Count);
        Assert.Equal([both, all], Collect(world.Query<Position, Velocity>()).OrderBy(e => e.Index));
        Assert.Equal([all], Collect(world.Query<Position, Velocity, Tag>()));
        Assert.Equal([all], Collect(world.Query<Velocity, Tag, Position>()));
    }

    [Fact]
    public void Query_AllowsDestroyingTheCurrentEntity()
    {
        var world = new World();
        for (var i = 0; i < 10; i++)
        {
            world.Add(world.CreateEntity(), new Position { X = i });
        }

        var visited = new List<float>();
        foreach (var e in world.Query<Position>())
        {
            visited.Add(world.Get<Position>(e).X);
            if (world.Get<Position>(e).X % 2 == 0)
            {
                world.DestroyEntity(e);
            }
        }

        Assert.Equal(10, visited.Distinct().Count());
        Assert.Equal(5, world.EntityCount);
    }

    [Fact]
    public void Query_EntitiesCreatedDuringIterationAreNotVisited()
    {
        var world = new World();
        world.Add(world.CreateEntity(), new Position());
        var count = 0;
        foreach (var _ in world.Query<Position>())
        {
            count++;
            world.Add(world.CreateEntity(), new Position());
        }

        Assert.Equal(1, count);
        Assert.Equal(2, world.Pool<Position>().Count);
    }

    [Fact]
    public void BoxedAdd_UsesTheRuntimeType()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddBoxed(e, typeof(Tag), new Tag());
        Assert.True(world.Has<Tag>(e));
        Assert.Throws<ArgumentException>(() => world.AddBoxed(e, typeof(Position), new Tag()));
        var runtimeType = typeof(Tag);
        Assert.True(world.Remove(e, runtimeType));
    }

    [Fact]
    public void Systems_RunInOrderStableForTies()
    {
        var log = new List<string>();
        var scheduler = new SystemScheduler();
        scheduler.Add(new Recorder("b", 10, log));
        scheduler.Add(new Recorder("a", 0, log));
        scheduler.Add(new Recorder("c", 10, log));
        scheduler.Add(new Recorder("d", -5, log) { Enabled = false });

        scheduler.Update(new World(), default);
        Assert.Equal(["a", "b", "c"], log);
        Assert.Throws<InvalidOperationException>(() => scheduler.Add(scheduler.Systems[0]));
    }

    private static List<Entity> Collect<TQuery>(TQuery query)
        where TQuery : struct
    {
        var result = new List<Entity>();
        switch (query)
        {
            case Query<Position> q:
                foreach (var e in q)
                {
                    result.Add(e);
                }

                break;
            case Query<Position, Velocity> q:
                foreach (var e in q)
                {
                    result.Add(e);
                }

                break;
            case Query<Position, Velocity, Tag> q:
                foreach (var e in q)
                {
                    result.Add(e);
                }

                break;
            case Query<Velocity, Tag, Position> q:
                foreach (var e in q)
                {
                    result.Add(e);
                }

                break;
            default:
                throw new NotSupportedException();
        }

        return result;
    }

    private sealed class Recorder(string name, int order, List<string> log) : GameSystem
    {
        public override int Order => order;

        public override void Update(World world, FrameTime time) => log.Add(name);
    }
}
