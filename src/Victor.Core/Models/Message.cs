namespace Victor.Core.Models;

public enum Role { User, Assistant }

public record Message(Role Role, string Content);
