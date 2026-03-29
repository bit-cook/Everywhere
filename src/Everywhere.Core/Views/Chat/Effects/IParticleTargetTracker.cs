namespace Everywhere.Views;

public interface IParticleTargetTracker
{
    bool TryGetTargetCenterOnScreen(out PixelPoint point);
    
    void OnParticleCompleted();
}