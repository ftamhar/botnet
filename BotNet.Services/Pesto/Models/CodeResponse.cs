﻿namespace BotNet.Services.Pesto.Models; 

public record CodeResponse(
	string Language,
	string Version,
	CodeOutput Compile,
	CodeOutput Runtime);
