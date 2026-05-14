namespace ClaudeUsageProjector.Predictor.Projection;

public interface IProjectionEngine
{
    Projection Project(ProjectionInputs inputs);
}
