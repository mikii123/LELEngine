using LELEngine;

public class DirectionalLight : Behaviour
{
    public static DirectionalLight This;

    public override void Awake()
    {
        This = this;
    }
}
