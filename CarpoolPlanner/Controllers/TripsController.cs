﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using CarpoolPlanner.Model;
using CarpoolPlanner.ViewModel;
using log4net;

namespace CarpoolPlanner.Controllers
{
    [Authorize]
    public class TripsController : CarpoolControllerBase
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(TripsController));

        public ActionResult Index()
        {
            var model = new TripsCombinedViewModel();
            using (var context = ApplicationDbContext.Create())
            {
                model.TripsModel = GetTripsViewModel(context);
            }
            return View(model);
        }

        [HttpPost]
        public ActionResult Index(TripsViewModel model)
        {
            // We should only be saving the attendance here,
            // so load the model form the DB and update the attendance values.
            // DON'T simly save the clientModel to the DB.
            if (model == null)
            {
                Response.StatusCode = 400;
                model = new TripsViewModel();
                model.SetMessage("Model not specified.", MessageType.Error);
                return Ng(model);
            }
            if (model.Trips == null)
            {
                model.SetMessage("No changes to save.", MessageType.Error);
                return Ng(model);
            }
            TripsViewModel serverModel;
            var user = AppUtils.CurrentUser;
            bool save = false;
            using (var context = ApplicationDbContext.Create())
            {
                serverModel = GetTripsViewModel(context);
                foreach (var serverUserTrip in from t in serverModel.Trips
                                         where t.UserTrips.Contains(user.Id)
                                         select t.UserTrips[user.Id])
                {
                    var clientTrip = model.Trips.FirstOrDefault(t => t.Id == serverUserTrip.TripId);
                    if (clientTrip == null || !clientTrip.UserTrips.Contains(user.Id))
                        continue;
                    var clientUserTrip = clientTrip.UserTrips[user.Id];
                    if (clientUserTrip.Attending != serverUserTrip.Attending)
                    {
                        serverUserTrip.Attending = clientUserTrip.Attending;
                        save = true;
                    }
                    if (serverUserTrip.Attending)
                    {
                        foreach (var serverUserTripRecurrence in serverUserTrip.Recurrences)
                        {
                            var clientTripRecurrence = clientTrip.Recurrences.FirstOrDefault(tr => tr.Id == serverUserTripRecurrence.TripRecurrenceId);
                            if (clientTripRecurrence == null || !clientTripRecurrence.UserTripRecurrences.Contains(user.Id))
                                continue;
                            var clientUserTripRecurrence = clientTripRecurrence.UserTripRecurrences[user.Id];
                            if (clientUserTripRecurrence.Attending != serverUserTripRecurrence.Attending)
                            {
                                serverUserTripRecurrence.Attending = clientUserTripRecurrence.Attending;
                                // Ensure the trip instance exists and the attendance status is correct
                                var instance = context.GetNextUserTripInstance(serverUserTripRecurrence.TripRecurrence, AppUtils.CurrentUser, TimeSpan.Zero);
                                if (instance == null)
                                    continue;
                                if (serverUserTripRecurrence.Attending)
                                {
                                    if (instance.Attending == false)
                                        instance.Attending = null;
                                    // Send a notification to the user in case we are past the notification time.
                                    var client = new NotificationServiceClient();
                                    client.UpdateTripInstance(instance.TripInstanceId, serverUserTripRecurrence.TripRecurrenceId);
                                }
                                else
                                {
                                    if (instance.Attending == null)
                                        instance.Attending = false;
                                }
                                save = true;
                            }
                        }
                    }
                    else
                    {
                        foreach (var recurrence in serverUserTrip.Recurrences.Where(utr => utr.Attending))
                        {
                            recurrence.Attending = false;
                            // Change the attendance status of any instances to false (unless they were already confirmed)
                            var instance = context.GetNextUserTripInstance(recurrence.TripRecurrence, AppUtils.CurrentUser, TimeSpan.Zero);
                            if (instance == null)
                                continue;
                            if (instance.Attending == null)
                                instance.Attending = false;
                            save = true;
                        }
                    }
                }
                if (save)
                    context.SaveChanges();
            }
            serverModel.SetMessage(save ? "Saved successfully." : "No changes to save.", MessageType.Success);
            return Ng(serverModel);
        }

        [HttpPost]
        public ActionResult CreateTrip(SaveTripViewModel model)
        {
            if (!AppUtils.IsUserAdmin())
            {
                Response.StatusCode = 403;
                model.SetMessage("You are not authorized to create trips.", MessageType.Error);
                log.Warn(model.Message);
                return Ng(model);
            }
            if (model.Trip == null)
            {
                Response.StatusCode = 400;
                model.SetMessage("Trip not specified.", MessageType.Error);
                log.Warn(model.Message);
                return Ng(model);
            }
            using (var context = ApplicationDbContext.Create())
            {
                context.Trips.Add(model.Trip);
                context.SaveChanges();
                model.SavedTrip = model.Trip;
                if (EnsureUserTrips(context, model.SavedTrip, AppUtils.CurrentUser))
                    context.SaveChanges();
            }
            model.Trip = new Trip { TimeZone = AppUtils.CurrentUser.TimeZone };
            model.Trip.Recurrences.Add(new TripRecurrence());
            model.SetMessage("Created successfully.", MessageType.Success);
            return Ng(model);
        }

        [HttpPost]
        public ActionResult UpdateTrip(SaveTripViewModel model)
        {
            var user = AppUtils.CurrentUser;
            if (user == null || !user.IsAdmin)
            {
                Response.StatusCode = 403;
                model.SetMessage("You are not authorized to save trips.", MessageType.Error);
                log.Warn(model.Message);
                return Ng(model);
            }
            if (model.Trip == null)
            {
                Response.StatusCode = 400;
                model.SetMessage("Trip not specified.", MessageType.Error);
                log.Warn(model.Message);
                return Ng(model);
            }
            using (var context = ApplicationDbContext.Create())
            {
                var changedIds = new List<Tuple<long, long>>();
                var serverTrip = context.Trips.Include(t => t.Recurrences).FirstOrDefault(t => t.Id == model.Trip.Id);
                context.UserTrips.Where(ut => ut.TripId == serverTrip.Id && ut.UserId == user.Id).Include(ut => ut.Recurrences).ToList();
                if (serverTrip == null)
                {
                    Response.StatusCode = 400;
                    model.SetMessage("Trip not found. It may have been deleted by another user.", MessageType.Error);
                    log.Warn(model.Message + " Trip ID: " + model.Trip.Id);
                    return Ng(model);
                }
                serverTrip.Location = model.Trip.Location;
                serverTrip.Name = model.Trip.Name;
                serverTrip.TimeZone = model.Trip.TimeZone;
                if (model.Trip.Recurrences == null)
                {
                    foreach (var recurrence in serverTrip.Recurrences.ToList())
                    {
                        var instance = context.GetNextTripInstance(recurrence, ApplicationDbContext.TripInstanceRemovalDelay);
                        if (instance != null && !instance.DriversPicked)
                            context.TripInstances.Remove(instance);
                        context.TripRecurrences.Remove(recurrence);
                    }
                }
                else
                {
                    foreach (var serverTripRecurrence in serverTrip.Recurrences.ToList())
                    {
                        var clientTripRecurrence = model.Trip.Recurrences.FirstOrDefault(tr => tr.Id == serverTripRecurrence.Id);
                        if (clientTripRecurrence == null)
                        {
                            // Delete recurrence and the next instance
                            var instance = context.GetNextTripInstance(serverTripRecurrence, ApplicationDbContext.TripInstanceRemovalDelay);
                            if (instance != null && !instance.DriversPicked)
                                context.TripInstances.Remove(instance);
                            context.TripRecurrences.Remove(serverTripRecurrence);
                        }
                        else
                        {
                            // Modify existing recurrence
                            serverTripRecurrence.End = clientTripRecurrence.End;
                            if (serverTripRecurrence.Every != clientTripRecurrence.Every
                                || serverTripRecurrence.Type != clientTripRecurrence.Type
                                || serverTripRecurrence.Start != clientTripRecurrence.Start)
                            {
                                // Also move the next instance of this recurrence if drivers have not been picked.
                                var instance = context.GetNextTripInstance(serverTripRecurrence, ApplicationDbContext.TripInstanceRemovalDelay);
                                serverTripRecurrence.Every = clientTripRecurrence.Every;
                                serverTripRecurrence.Type = clientTripRecurrence.Type;
                                serverTripRecurrence.Start = clientTripRecurrence.Start;
                                if (instance != null && !instance.DriversPicked)
                                {
                                    var nextDate = serverTripRecurrence.GetNextInstanceDate(DateTime.UtcNow - ApplicationDbContext.TripInstanceRemovalDelay, serverTrip.DateTimeZone);
                                    if (nextDate.HasValue)
                                    {
                                        instance.Date = nextDate.Value;
                                        changedIds.Add(new Tuple<long,long>(instance.Id, serverTripRecurrence.Id));
                                    }
                                    else
                                    {
                                        context.TripInstances.Remove(instance);
                                    }
                                }
                            }
                        }
                    }
                    foreach (var clientTripRecurrence in model.Trip.Recurrences.Where(t1 => serverTrip.Recurrences.All(t2 => t1.Id != t2.Id)))
                    {
                        // Create new recurrence
                        serverTrip.Recurrences.Add(clientTripRecurrence);
                    }
                }
                serverTrip.Status = model.Trip.Status;
                context.SaveChanges();
                model.SavedTrip = serverTrip;
                if (EnsureUserTrips(context, model.SavedTrip, user))
                    context.SaveChanges();

                // Tell the notification service to update the notification times
                var client = new NotificationServiceClient();
                foreach (var ids in changedIds)
                    client.UpdateTripInstance(ids.Item1, ids.Item2);
            }
            model.Trip = null;
            model.SetMessage("Saved successfully.", MessageType.Success);
            return Ng(model);
        }

        [HttpDelete]
        public ActionResult DeleteTrip(int id)
        {
            var model = new TripsViewModel();
            if (!AppUtils.IsUserAdmin())
            {
                Response.StatusCode = 403;
                model.SetMessage("You are not authorized to delete trips.", MessageType.Error);
                log.Warn(model.Message);
                return Ng(model);
            }
            using (var context = ApplicationDbContext.Create())
            {
                var trip = context.Trips.Find(id);
                if (trip != null)
                {
                    context.Trips.Remove(trip);
                    context.SaveChanges();
                    model.SetMessage("Deleted successfully.", MessageType.Success);
                }
                else
                {
                    model.SetMessage("Trip does not exist. It may have been deleted by another user.", MessageType.Error);
                }
            }
            return Ng(model);
        }

        private static TripsViewModel GetTripsViewModel(ApplicationDbContext context)
        {
            var model = new TripsViewModel();
            var user = AppUtils.CurrentUser;
            model.Trips = context.Trips.Include(t => t.Recurrences).ToList();
            context.GetUserTrips(user.Id).Include(ut => ut.Recurrences).ToList();
            foreach (var trip in model.Trips)
                trip.Recurrences = trip.Recurrences.OrderBy(tr => tr.Start).ToList();

            bool save = false;
            // Create UserTrips and UserTripRecurrences if they don't exist for this user.
            foreach (var trip in model.Trips)
            {
                save = EnsureUserTrips(context, trip, user) || save;
            }
            if (save)
                context.SaveChanges();
            return model;
        }

        private static bool EnsureUserTrips(ApplicationDbContext context, Trip trip, User user)
        {
            bool save = false;
            UserTrip userTrip;
            if (trip.UserTrips.Contains(user.Id))
            {
                userTrip = trip.UserTrips[user.Id];
            }
            else
            {
                userTrip = new UserTrip { Attending = false, UserId = AppUtils.CurrentUser.Id, TripId = trip.Id };
                trip.UserTrips.Add(userTrip);
                context.UserTrips.Add(userTrip);
                save = true;
            }
            foreach (var tripRecurrence in trip.Recurrences)
            {
                if (userTrip.Recurrences.All(utr => utr.TripRecurrenceId != tripRecurrence.Id))
                {
                    var userTripRecurrence = new UserTripRecurrence
                    {
                        Attending = false,
                        TripId = trip.Id,
                        TripRecurrenceId = tripRecurrence.Id,
                        UserId = AppUtils.CurrentUser.Id
                    };
                    userTrip.Recurrences.Add(userTripRecurrence);
                    context.UserTripRecurrences.Add(userTripRecurrence);
                    save = true;
                }
            }
            return save;
        }
    }
}
