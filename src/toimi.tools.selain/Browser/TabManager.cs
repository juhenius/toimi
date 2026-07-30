namespace toimi.tools.selain.Browser;

/// <summary>
/// Owns the open tabs. Tab GUIDs double as the capability token for the HTTP
/// viewer endpoints. ActionLock serializes every mutating browser operation —
/// snapshot refs belong to the active tab, so concurrent cross-tab actions
/// would race ref validity.
/// </summary>
public sealed class TabManager(SelainOptions options)
{
  private readonly List<TabEntry> _tabs = [];
  private readonly Lock _gate = new();
  private Guid? _activeId;

  public SemaphoreSlim ActionLock { get; } = new(1, 1);

  public sealed class TabEntry
  {
    public required Guid Id { get; init; }
    public required IPageSession Session { get; init; }
    public string? LastShownHash { get; set; }
    public string? DialogNote { get; set; }
  }

  public int Count
  {
    get
    {
      lock (_gate)
      {
        return _tabs.Count;
      }
    }
  }

  public TabEntry? Active
  {
    get
    {
      lock (_gate)
      {
        return _tabs.FirstOrDefault(t => t.Id == _activeId);
      }
    }
  }

  public Guid Adopt(IPageSession session)
  {
    lock (_gate)
    {
      var existing = _tabs.FirstOrDefault(t => ReferenceEquals(t.Session.NativeHandle, session.NativeHandle));
      if (existing is not null)
      {
        return existing.Id;
      }

      var entry = new TabEntry { Id = Guid.NewGuid(), Session = session };
      _tabs.Add(entry);
      _activeId ??= entry.Id;
      return entry.Id;
    }
  }

  public Guid? FindByHandle(object nativeHandle)
  {
    lock (_gate)
    {
      return _tabs.FirstOrDefault(t => ReferenceEquals(t.Session.NativeHandle, nativeHandle))?.Id;
    }
  }

  public TabEntry? Get(Guid id)
  {
    lock (_gate)
    {
      return _tabs.FirstOrDefault(t => t.Id == id);
    }
  }

  public IReadOnlyList<TabEntry> List()
  {
    lock (_gate)
    {
      return [.. _tabs];
    }
  }

  public bool Switch(Guid id)
  {
    lock (_gate)
    {
      if (_tabs.All(t => t.Id != id))
      {
        return false;
      }

      _activeId = id;
      return true;
    }
  }

  public async Task<bool> CloseAsync(Guid id)
  {
    TabEntry? entry;
    lock (_gate)
    {
      entry = _tabs.FirstOrDefault(t => t.Id == id);
      if (entry is null)
      {
        return false;
      }

      _tabs.Remove(entry);
      if (_activeId == id)
      {
        _activeId = _tabs.FirstOrDefault()?.Id;
      }
    }

    await entry.Session.CloseAsync();
    return true;
  }

  /// <summary>
  /// Reap a tab whose underlying page closed itself (user close, window.close(),
  /// per-page crash). Unlike CloseAsync this does NOT close the session — the
  /// page is already gone — it just drops the bookkeeping so idle shutdown can
  /// eventually fire. Falls back active like CloseAsync; unknown handle is a no-op.
  /// </summary>
  public bool RemoveByHandle(object nativeHandle)
  {
    lock (_gate)
    {
      var entry = _tabs.FirstOrDefault(t => ReferenceEquals(t.Session.NativeHandle, nativeHandle));
      if (entry is null)
      {
        return false;
      }

      _tabs.Remove(entry);
      if (_activeId == entry.Id)
      {
        _activeId = _tabs.FirstOrDefault()?.Id;
      }

      return true;
    }
  }

  public void ResetAll()
  {
    lock (_gate)
    {
      _tabs.Clear();
      _activeId = null;
    }
  }

  public string ViewerUrl(Guid id)
  {
    return $"{options.PublicBaseUrl.TrimEnd('/')}/tabs/{id}/view";
  }

  public void NoteDialog(Guid id, string note)
  {
    lock (_gate)
    {
      var entry = _tabs.FirstOrDefault(t => t.Id == id);
      entry?.DialogNote = note;
    }
  }

  public string? TakeDialogNote(Guid id)
  {
    lock (_gate)
    {
      var entry = _tabs.FirstOrDefault(t => t.Id == id);
      var note = entry?.DialogNote;
      entry?.DialogNote = null;

      return note;
    }
  }
}
