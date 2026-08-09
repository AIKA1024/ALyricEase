namespace ALyricEase.Services.Audio;

/// <summary>播放状态机:Idle → Loading → Playing / Paused。</summary>
public enum PlaybackState
{
    Idle,
    Loading,
    Playing,
    Paused,
}
