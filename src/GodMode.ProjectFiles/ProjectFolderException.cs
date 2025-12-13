namespace GodMode.ProjectFiles;

/// <summary>
/// Base exception for project folder operations.
/// </summary>
public class ProjectFolderException : Exception
{
    /// <summary>
    /// Gets the project ID associated with this exception, if available.
    /// </summary>
    public string? ProjectId { get; }

    /// <summary>
    /// Initializes a new instance of the ProjectFolderException class.
    /// </summary>
    public ProjectFolderException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the ProjectFolderException class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ProjectFolderException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ProjectFolderException class with a specified error message and project ID.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="projectId">The project ID associated with the error.</param>
    public ProjectFolderException(string message, string projectId) : base(message)
    {
        ProjectId = projectId;
    }

    /// <summary>
    /// Initializes a new instance of the ProjectFolderException class with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ProjectFolderException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ProjectFolderException class with a specified error message, project ID, and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="projectId">The project ID associated with the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ProjectFolderException(string message, string projectId, Exception innerException) : base(message, innerException)
    {
        ProjectId = projectId;
    }
}

/// <summary>
/// Exception thrown when a project folder is corrupted or has invalid structure.
/// </summary>
public class CorruptProjectException : ProjectFolderException
{
    /// <summary>
    /// Initializes a new instance of the CorruptProjectException class.
    /// </summary>
    public CorruptProjectException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the CorruptProjectException class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public CorruptProjectException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the CorruptProjectException class with a specified error message and project ID.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="projectId">The project ID associated with the error.</param>
    public CorruptProjectException(string message, string projectId) : base(message, projectId)
    {
    }

    /// <summary>
    /// Initializes a new instance of the CorruptProjectException class with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public CorruptProjectException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the CorruptProjectException class with a specified error message, project ID, and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="projectId">The project ID associated with the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public CorruptProjectException(string message, string projectId, Exception innerException) : base(message, projectId, innerException)
    {
    }
}
