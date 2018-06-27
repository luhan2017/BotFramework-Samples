﻿using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Choices;
using Microsoft.Bot.Schema;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DataTypes.TimexExpression;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ContosoCafe
{
    /// <summary>
    /// Defines the dialog for booking a table.
    /// </summary>
    public class BookATable : DialogSet
    {
        /// <summary>
        /// Defines a singleton instance of the dialog.
        /// </summary>
        public static BookATable Instance { get; } = new Lazy<BookATable>(new BookATable()).Value;

        /// <summary>
        /// The names of the inputs and prompts in this dialog.
        /// </summary>
        /// <remarks>We'll store the information gathered using these same names.</remarks>
        public struct Keys
        {
            /// <summary>
            ///  Key to use for LUIS entities as input.
            /// </summary>
            public const string LuisArgs = "LuisEntities";

            public const string Location = "location";
            public const string DateTime = "dateTime";
            public const string Guests = "numberOfGuests";
            public const string Name = "reservationName";
            public const string Confirm = "confirmation";
        }

        /// <summary>
        /// The list of store locations.
        /// </summary>
        public static IReadOnlyList<string> Locations { get; } =
            new List<string> { "Bellevue", "Redmond", "Renton", "Seattle" };

        /// <summary>
        /// The validatior to use with the reservation date and time prompt.
        /// </summary>
        /// <param name="context">The current turn context.</param>
        /// <param name="toValidate">The input to be validated.</param>
        /// <returns>An updated <paramref name="toValidate"/> value that sets the object's 
        /// Prompt status to indicate whether the value validates.</returns>
        /// <remarks>Valid dates are evenings within the next 2 weeks.</remarks>
        private static Task DateTimeValidator(ITurnContext context, DateTimeResult toValidate)
        {
            if (toValidate.Resolution.Count is 0)
            {
                toValidate.Status = PromptStatus.NotRecognized;
                return Task.CompletedTask;
            }

            var candidates = toValidate.Resolution.Select(res => res.Timex).ToList();

            // Find any matches within dates from this week or next (not in the past), and evenings only.
            var resolution = ResolveTime(candidates);
            if (resolution != null)
            {
                toValidate.Resolution.Clear();
                toValidate.Resolution.Add(resolution);
                toValidate.Status = PromptStatus.Recognized;
            }
            else
            {
                toValidate.Status = PromptStatus.NotRecognized;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Compares a set of candidate date time strings against our validation constraints, and
        /// returns a value that matches; or null, if no candidates meet the constraints.
        /// </summary>
        /// <param name="candidates">The candidate strings.</param>
        /// <returns>A value that matches; or null, if no candidates meet the constraints.</returns>
        /// <remarks>Valid dates are evenings within the next 2 weeks.</remarks>
        private static DateTimeResult.DateTimeResolution ResolveTime(IEnumerable<string> candidates)
        {
            // Find any matches within dates from this week or next (not in the past), and evenings only.
            var constraints = new[]
            {
                TimexCreator.NextWeeksFromToday(2),
                TimexCreator.Evening
            };
            List<TimexProperty> resolutions = null;
            try
            {
                resolutions = TimexRangeResolver.Evaluate(candidates, constraints);
            }
            catch
            {
                return null;
            }

            if (resolutions.Count is 0)
            {
                return null;
            }

            // Use the first recognized value for the reservation time.
            var timex = resolutions[0];
            return new DateTimeResult.DateTimeResolution
            {
                Start = timex.ToNaturalLanguage(DateTime.Now),
                End = timex.ToNaturalLanguage(DateTime.Now),
                Value = timex.ToNaturalLanguage(DateTime.Now),
                Timex = timex.TimexValue
            };
        }

        /// <summary>
        /// The validatior to use with the number of guests prompt.
        /// </summary>
        /// <param name="context">The current turn context.</param>
        /// <param name="toValidate">The input to be validated.</param>
        /// <returns>An updated <paramref name="toValidate"/> value that sets the object's 
        /// Prompt status to indicate whether the value validates.</returns>
        /// <remarks>Valid party sizes are 1 through 12.</remarks>
        private static Task GuestsValidator(ITurnContext context, NumberResult<int> toValidate)
        {
            if (toValidate.Value < 1)
            {
                toValidate.Status = PromptStatus.TooSmall;
            }
            else if (toValidate.Value > 12)
            {
                toValidate.Status = PromptStatus.TooBig;
            }
            else
            {
                toValidate.Status = PromptStatus.Recognized;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Creates a new dialog instance.
        /// </summary>
        private BookATable()
        {
            // Add the prompts we'll be using in our dialog.
            Add(Keys.Location, new ChoicePrompt(Culture.English));
            Add(Keys.DateTime, new DateTimePrompt(Culture.English, DateTimeValidator));
            Add(Keys.Guests, new NumberPrompt<int>(Culture.English, GuestsValidator));
            Add(Keys.Name, new TextPrompt());
            Add(Keys.Confirm, new ConfirmPrompt(Culture.English));

            // Define and add the waterfall steps for our dialog.
            Add(nameof(BookATable), new WaterfallStep[]
            {
                // Begin booking a table.
                async (dc, args, next) =>
                {
                    // Initialize state.
                    dc.ActiveDialog.State = new Dictionary<string,object>();

                    // Add any LUIS entities to the active dialog state.
                    if(args!=null && args.ContainsKey(Keys.LuisArgs))
                    {
                        // Remove any values that don't validate, and convert the remainder to a dictionary.
                        var entities = (CafeLuisModel._Entities)args[Keys.LuisArgs];
                        dc.ActiveDialog.State[Keys.LuisArgs] = ValidateLuisArgs(entities);
                    }

                    // Check whether input for location already exists
                    var luisargs = GetLuisArgs(dc);
                    if (luisargs.ContainsKey(Keys.Location))
                    {
                        // If we have an entity value from LUIS for this step, pass this value to the next step
                        // as if it were output from the prompt in this step.
                        await next(new Dictionary<string,object>
                        {
                            ["Value"] = new FoundChoice { Value = luisargs[Keys.Location] as string }
                        });
                    }
                    else
                    {
                        // Query for location.
                        var retryPrompt = MessageFactory.SuggestedActions(
                            Locations.ToList(), text: "Please select one of our locations.") as Activity;
                        await dc.Prompt(Keys.Location,
                            "Did you have a location in mind?", new ChoicePromptOptions
                            {
                                RetryPromptActivity = retryPrompt,
                                Choices = ChoiceFactory.ToChoices(new List<string>(Locations))
                            });
                    }
                },
                async (dc, args, next) =>
                {
                    // Update state with the location.
                    var answer = args["Value"] as FoundChoice;
                    dc.ActiveDialog.State[Keys.Location] = answer.Value;

                    // Check whether input for reservation time already exists
                    var luisargs = GetLuisArgs(dc);
                    if (luisargs.ContainsKey(Keys.DateTime))
                    {
                        // If we have an entity value from LUIS for this step, pass this value to the next step
                        // as if it were output from the prompt in this step.
                        await next(new Dictionary<string,object>
                        {
                            ["Resolution"] = new List<DateTimeResult.DateTimeResolution>
                            {
                                new DateTimeResult.DateTimeResolution
                                {
                                    Value = luisargs[Keys.DateTime] as string
                                }
                            }
                        });
                    }
                    else
                    {
                        // Query for date and time.
                        await dc.Prompt(Keys.DateTime,
                            "When will the reservation be for?", new PromptOptions
                            {
                                RetryPromptString = "Please enter a date and time for the reservation.\n\n" +
                                "We take reservations within two weeks of today, and evenings only.",
                            });
                    }
                },
                async (dc, args, next) =>
                {
                    // Update state with the date and time.
                    // The prompt can return multiple interpretations of the time entered.
                    // For now, just use the first one.
                    var answer = args["Resolution"] as List<DateTimeResult.DateTimeResolution>;
                    dc.ActiveDialog.State[Keys.DateTime] = answer[0].Value;

                    // Check whether input for the number of guests already exists
                    var luisargs = GetLuisArgs(dc);
                    if (luisargs.ContainsKey(Keys.Guests))
                    {
                        // If we have an entity value from LUIS for this step, pass this value to the next step
                        // as if it were output from the prompt in this step.
                        await next(new Dictionary<string,object>
                        {
                            ["Value"] = (int)luisargs[Keys.Guests]
                        });
                    }
                    else
                    {
                        // Query for the number of guests.
                        await dc.Prompt(Keys.Guests,
                            "How many guests?", new PromptOptions
                            {
                                RetryPromptString = "Please enter the number of people that the reservation is for.\n\n" +
                                "We can take reservations for parties of up to 12.",
                            });
                    }
                },
                async (dc, args, next) =>
                {
                    // Update state with the number of guests.
                    var answer = (int)args["Value"];
                    dc.ActiveDialog.State[Keys.Guests] = answer;

                    // Check whether input for the reservation name already exists
                    var luisargs = GetLuisArgs(dc);
                    if (luisargs.ContainsKey(Keys.Name))
                    {
                        // If we have an entity value from LUIS for this step, pass this value to the next step
                        // as if it were output from the prompt in this step.
                        await next(new Dictionary<string,object>
                        {
                            ["Value"] = luisargs[Keys.Name] as string
                        });
                    }
                    else
                    {
                        // Query for a name for the resevation.
                        await dc.Prompt(Keys.Name,
                            "What name should I book the table under?", new PromptOptions
                            {
                                RetryPromptString = "Please enter a name for the reservation.",
                            });
                    }
                },
                async (dc, args, next) =>
                {
                    // Update state with the name for the reservation.
                    var answer = args["Value"] as string;
                    dc.ActiveDialog.State[Keys.Name] = answer;

                    // Confirm the resevation.
                    await dc.Prompt(Keys.Confirm,
                        $"Ok. Should I go ahead and book a table " +
                        $"for {dc.ActiveDialog.State[Keys.Guests]} " +
                        $"at {dc.ActiveDialog.State[Keys.Location]} " +
                        $"for {dc.ActiveDialog.State[Keys.DateTime]} " +
                        $"for {dc.ActiveDialog.State[Keys.Name]}?", new PromptOptions
                        {
                            RetryPromptString = "I'm sorry, should I make the reservation for you? " +
                            "Please enter `yes` or `no`.",
                        });
                },
                async (dc, args, next) =>
                {
                    // Book the table or cancel the reservation.
                    var confirmed = (bool)args["Confirmation"];
                    if (confirmed)
                    {
                        // Send a confirmation message: the typing activity indicates to the user that
                        // the bot is working on something, the delay simulates a process (filing the reservation)
                        // that takes some time, and the message simulates a confirmation message generated
                        // by the process.
                        var typing = Activity.CreateTypingActivity();
                        var delay = new Activity { Type = "delay", Value = 3000 };
                        await dc.Context.SendActivities(
                            new IActivity[]
                            {
                                typing, delay,
                                MessageFactory.Text("Your table is booked. Reference number: #K89HG38SZ")
                            });
                    }
                    else
                    {
                        // Cancel the reservation.
                        await dc.Context.SendActivity("Okay. We have canceled the reservation.");
                    }
                }
            });
        }

        /// <summary>
        /// Helper method to get the LUIS arguments from the dialog state.
        /// </summary>
        /// <param name="dc">The dialog context from which to retrieve state.</param>
        /// <returns>A dictionary of values extracted from any LUIS entities added to the dialog via the call to dc.Begin.</returns>
        private static Dictionary<string, object> GetLuisArgs(DialogContext dc)
        {
            return dc.ActiveDialog.State[Keys.LuisArgs] as Dictionary<string, object>;
        }

        /// <summary>
        /// Check whether each entity is valid and return valid ones in a dictionary.
        /// </summary>
        /// <param name="entities">The LUIS entities from the input arguments.</param>
        /// <returns>A dictionary of the valid entities.</returns>
        private Dictionary<string, object> ValidateLuisArgs(CafeLuisModel._Entities entities)
        {
            var result = new Dictionary<string, object>();

            // Check location.
            if (Locations.Contains(entities?.location?[0]?[0], StringComparer.InvariantCultureIgnoreCase))
            {
                result[Keys.Location] = entities.location[0][0] as string;
            }

            // Check time.
            if (entities?.datetime?.FirstOrDefault()?.Expressions.Any() is true)
            {
                var candidates = entities.datetime[0].Expressions;
                var resolution = ResolveTime(candidates);
                if (resolution != null)
                {
                    result[Keys.DateTime] = resolution.Value;
                }
            }

            // Check number of guests.
            if (entities?.number?.Any() is true)
            {
                var number = entities.number.FirstOrDefault(n => n > 0 && n < 13);
                if (number != 0)
                {
                    // LUIS recognizes numbers as doubles. Convert to int.
                    result[Keys.Guests] = (int)number;
                }
            }

            // Check reservation name.
            if (entities?.reservationName?.Any() is true)
            {
                var name = entities.reservationName.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                if (name != null)
                {
                    result[Keys.Name] = name;
                }
            }

            return result;
        }
    }
}