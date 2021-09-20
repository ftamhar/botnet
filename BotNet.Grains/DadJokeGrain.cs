﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BotNet.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace BotNet.Grains {
	public class DadJokeGrain : Grain, IDadJokeGrain {
		private const string DAD_JOKES_ENDPOINT = "https://jokesbapak2.herokuapp.com/v1";
		private const int MAX_JOKES = 6;
		private readonly IServiceProvider _serviceProvider;
		private int? _jokeCount;
		private readonly HashSet<int> _jokeIds = new();
		private DateTime? _lastRandomized;

		public DadJokeGrain(
			IServiceProvider serviceProvider
		) {
			_serviceProvider = serviceProvider;
		}

		public async Task<ImmutableList<(string Id, string Url)>> GetRandomJokesAsync() {
			if (_jokeIds.Count > 0
				&& _lastRandomized.HasValue
				&& _lastRandomized.Value.ToString("HH:mm")[..^2] == DateTime.Now.ToString("HH:mm")[..^2]) {
				return _jokeIds.Select(jokeId => (Id: $"jokesbapack{jokeId}", Url: $"{DAD_JOKES_ENDPOINT}/id/{jokeId}")).ToImmutableList();
			}
			if (_jokeCount == null) {
				DadJokeMetadata? metadata = await _serviceProvider
					.GetRequiredService<HttpClient>()
					.GetFromJsonAsync<DadJokeMetadata>($"{DAD_JOKES_ENDPOINT}/total");

				if (metadata == null || metadata.JokeCount <= 0) return ImmutableList<(string Id, string Url)>.Empty;
				_jokeCount = metadata.JokeCount;
			}

			// Let's not risk the collisions
			if (_jokeCount < 100) return ImmutableList<(string Id, string Url)>.Empty;

			while (_jokeIds.Count < MAX_JOKES) {
				_jokeIds.Add(Random.Shared.Next(_jokeCount.Value));
			}
			_lastRandomized = DateTime.Now;
			return _jokeIds.Select(jokeId => (Id: $"jokesbapack{jokeId}", Url: $"{DAD_JOKES_ENDPOINT}/id/{jokeId}")).ToImmutableList();
		}

		private record DadJokeMetadata(
			[property: JsonPropertyName("message")] int JokeCount
		);
	}
}