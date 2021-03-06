﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using Orleans;
using OrleansXO.GrainInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OrleansXO.Grains
{
    /// <summary>
    /// Orleans grain implementation class PlayerGrain
    /// </summary>
    public class PlayerGrain : Orleans.Grain, IPlayerGrain
    {
        private List<Guid> ListOfActiveGames { get; set; }
        private List<Guid> ListOfPastGames { get; set; }

        private int wins;
        private int loses;
        private int gamesStarted;

        private string username;

        public override Task OnActivateAsync()
        {
            this.ListOfActiveGames = new List<Guid>();
            this.ListOfPastGames = new List<Guid>();

            this.wins = 0;
            this.loses = 0;
            this.gamesStarted = 0;

            return base.OnActivateAsync();
        }

        public async Task<PairingSummary[]> GetAvailableGames()
        {
            var grain = GrainFactory.GetGrain<IPairingGrain>(0);
            return (await grain.GetGames()).Where(x => !this.ListOfActiveGames.Contains(x.GameId)).ToArray();
        }

        // create a new game, and add oursleves to that game
        public async Task<Guid> CreateGame()
        {
            this.gamesStarted += 1;

            var gameId = Guid.NewGuid();
            var gameGrain = GrainFactory.GetGrain<IGameGrain>(gameId);  // create new game

            // add ourselves to the game
            var playerId = this.GetPrimaryKey();  // our player id
            await gameGrain.AddPlayerToGame(playerId);
            this.ListOfActiveGames.Add(gameId);
            var name = this.username + "'s " + AddOrdinalSuffix(this.gamesStarted.ToString()) + " game";
            await gameGrain.SetName(name);

            var pairingGrain = GrainFactory.GetGrain<IPairingGrain>(0);
            await pairingGrain.AddGame(gameId, name);

            return gameId;
        }


        // join a game that is awaiting players
        public async Task<GameState> JoinGame(Guid gameId)
        {
            var gameGrain = GrainFactory.GetGrain<IGameGrain>(gameId);

            var state = await gameGrain.AddPlayerToGame(this.GetPrimaryKey());
            this.ListOfActiveGames.Add(gameId);

            var pairingGrain = GrainFactory.GetGrain<IPairingGrain>(0);
            await pairingGrain.RemoveGame(gameId);

            return state;
        }


        // leave game when it is over
        public Task LeaveGame(Guid gameId, GameOutcome outcome)
        {
            // manage game list

            ListOfActiveGames.Remove(gameId);
            ListOfPastGames.Add(gameId);

            // manage running total

            if (outcome == GameOutcome.Win)
                wins++;
            if (outcome == GameOutcome.Lose)
                loses++;

            return TaskDone.Done;
        }

        public async Task<List<GameSummary>> GetGameSummaries()
        {
            var tasks = new List<Task<GameSummary>>();
            foreach (var gameId in this.ListOfActiveGames)
            {
                var game = GrainFactory.GetGrain<IGameGrain>(gameId);
                tasks.Add(game.GetSummary(this.GetPrimaryKey()));
            }
            await Task.WhenAll(tasks);
            return tasks.Select(x => x.Result).ToList();
        }

        public Task SetUsername(string name)
        {
            this.username = name;
            return TaskDone.Done;
        }

        public Task<string> GetUsername()
        {
            return Task.FromResult(this.username);
        }


        private static string AddOrdinalSuffix(string number)
        {

            int n = int.Parse(number);
            int nMod100 = n % 100;

            if (nMod100 >= 11 && nMod100 <= 13)
                return String.Concat(number, "th");

            switch (n % 10)
            {
                case 1:
                    return String.Concat(number, "st");
                case 2:
                    return String.Concat(number, "nd");
                case 3:
                    return String.Concat(number, "rd");
                default:
                    return String.Concat(number, "th");
            }

        }

    }
}
